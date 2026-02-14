using System.Data;
using Momentum.Messaging;
using Momentum.Messaging.Abstractions;
using Momentum.Messaging.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: MomentumMediator]


// ══════════════════════════════════════════════════════════════════════════════
// MODE 1: TransactionalByDefault (default)
//   → All handlers join the ambient transaction
//   → [NonTransactional] opts out specific handlers
// ══════════════════════════════════════════════════════════════════════════════

// builder.Services.AddMomentumOutbox();                    // TransactionalByDefault is the default
//
// or explicitly:
//
// builder.Services.AddMomentumOutbox(outbox =>
// {
//     outbox.TransactionMode = TransactionMode.TransactionalByDefault;
// });
//
// CreateOrderHandler         → transactional (default)
// SendNotificationHandler    → [NonTransactional] → independent
// ReserveInventoryHandler    → transactional (default)


// ══════════════════════════════════════════════════════════════════════════════
// MODE 2: NonTransactionalByDefault
//   → All handlers run independently by default
//   → [Transactional] opts in specific handlers
// ══════════════════════════════════════════════════════════════════════════════

// builder.Services.AddMomentumOutbox(outbox =>
// {
//     outbox.TransactionMode = TransactionMode.NonTransactionalByDefault;
// });
//
// CreateOrderHandler         → [Transactional] → joins ambient
// SendNotificationHandler    → independent (default)
// ReserveInventoryHandler    → [Transactional] → joins ambient


// ══════════════════════════════════════════════════════════════════════════════
// DI Setup (using Mode 1 for this example)
// ══════════════════════════════════════════════════════════════════════════════

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMomentum(momentum =>
{
    momentum.AddBehavior(typeof(OutboxBehavior<,>));
});

builder.Services.AddMomentumOutbox(outbox =>
{
    outbox.TransactionMode = TransactionMode.TransactionalByDefault;
});

var app = builder.Build();
await app.RunAsync;


// ══════════════════════════════════════════════════════════════════════════════
// Messages
// ══════════════════════════════════════════════════════════════════════════════

public sealed record CreateOrder(string ProductId, int Quantity) : IRequest<OrderResult>;
public sealed record OrderResult(Guid OrderId);
public sealed record SendNotification(Guid OrderId, string Email) : IRequest;
public sealed record ReserveInventory(Guid OrderId, string ProductId, int Quantity) : IRequest;

public sealed record OrderCreatedEvent(Guid OrderId, string ProductId);
public sealed record NotificationSentEvent(Guid OrderId, string Email);
public sealed record InventoryReservedEvent(Guid OrderId, string ProductId, int Quantity);


// ══════════════════════════════════════════════════════════════════════════════
// Handler A — transactional (default in Mode 1, no attribute needed)
//           — in Mode 2, would need [Transactional]
// ══════════════════════════════════════════════════════════════════════════════

// [Transactional]  ← only needed in Mode 2 (NonTransactionalByDefault)
public sealed class CreateOrderHandler
{
    private readonly IDbConnection _db;
    private readonly IOutboxCollector _outbox;
    private readonly IOutboxTransactionAccessor _tx;
    private readonly IMediator _mediator;

    public CreateOrderHandler(
        IDbConnection db, IOutboxCollector outbox,
        IOutboxTransactionAccessor tx, IMediator mediator)
    {
        _db = db; _outbox = outbox; _tx = tx; _mediator = mediator;
    }

    public async Task<OrderResult> HandleAsync(CreateOrder request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        var transaction = _tx.Current!.Transaction;

        // await _db.ExecuteAsync("INSERT INTO Orders ...", new { ... }, transaction);

        _outbox.Add(new OrderCreatedEvent(orderId, request.ProductId), "orders.created");

        await _mediator.SendAsync(new SendNotification(orderId, "user@example.com"), ct);
        await _mediator.SendAsync(new ReserveInventory(orderId, request.ProductId, request.Quantity), ct);

        return new OrderResult(orderId);
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// Handler B — non-transactional (opted out in Mode 1, default in Mode 2)
// ══════════════════════════════════════════════════════════════════════════════

[NonTransactional]   // ← only needed in Mode 1 (TransactionalByDefault)
public sealed class SendNotificationHandler
{
    private readonly IOutboxCollector _outbox;
    public SendNotificationHandler(IOutboxCollector outbox) => _outbox = outbox;

    public Task<Unit> HandleAsync(SendNotification request, CancellationToken ct)
    {
        _outbox.Add(new NotificationSentEvent(request.OrderId, request.Email), "notifications.sent");
        return Unit.Task;
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// Handler C — transactional (default in Mode 1, needs [Transactional] in Mode 2)
// ══════════════════════════════════════════════════════════════════════════════

// [Transactional]  ← only needed in Mode 2 (NonTransactionalByDefault)
public sealed class ReserveInventoryHandler
{
    private readonly IDbConnection _db;
    private readonly IOutboxCollector _outbox;
    private readonly IOutboxTransactionAccessor _tx;

    public ReserveInventoryHandler(IDbConnection db, IOutboxCollector outbox, IOutboxTransactionAccessor tx)
    {
        _db = db; _outbox = outbox; _tx = tx;
    }

    public async Task<Unit> HandleAsync(ReserveInventory request, CancellationToken ct)
    {
        var transaction = _tx.Current!.Transaction;
        // await _db.ExecuteAsync("UPDATE Inventory ...", new { ... }, transaction);

        _outbox.Add(
            new InventoryReservedEvent(request.OrderId, request.ProductId, request.Quantity),
            "inventory.reserved");

        await Task.CompletedTask;
        return Unit.Value;
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// SUMMARY
// ══════════════════════════════════════════════════════════════════════════════
//
// ┌──────────────────────────────────┬──────────────────────┬─────────────────────────┐
// │ Handler                          │ Mode 1 (Tx default)  │ Mode 2 (NonTx default)  │
// ├──────────────────────────────────┼──────────────────────┼─────────────────────────┤
// │ CreateOrderHandler               │ transactional         │ needs [Transactional]   │
// │ SendNotificationHandler          │ needs [NonTx]         │ independent (default)   │
// │ ReserveInventoryHandler          │ transactional         │ needs [Transactional]   │
// └──────────────────────────────────┴──────────────────────┴─────────────────────────┘
//
// Pick the mode that matches your app:
//   - Mostly DB-heavy handlers → TransactionalByDefault (opt out the exceptions)
//   - Mostly fire-and-forget   → NonTransactionalByDefault (opt in the critical ones)
