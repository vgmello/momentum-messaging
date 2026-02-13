using Microsoft.Extensions.Logging;
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging.Outbox;

/// <summary>
/// Wraps the message processor with inbox deduplication.
/// Before processing a message, it checks if the message ID was already handled.
/// </summary>
public sealed class InboxMessageProcessor : IMessageProcessor
{
    private readonly IInboxStore _inboxStore;
    private readonly IMessageProcessor _inner;
    private readonly ILogger<InboxMessageProcessor> _logger;

    public InboxMessageProcessor(
        IInboxStore inboxStore,
        IMessageProcessor inner,
        ILogger<InboxMessageProcessor> logger)
    {
        _inboxStore = inboxStore;
        _inner = inner;
        _logger = logger;
    }

    public async Task ProcessAsync(
        MessageEnvelope envelope,
        IDeliveryContext context,
        CancellationToken ct = default)
    {
        var messageId = envelope.MessageId;

        // Try to claim — returns false if already processed (duplicate)
        var claimed = await _inboxStore.TryClaimAsync(messageId, ct: ct);

        if (!claimed)
        {
            _logger.LogDebug("Inbox: duplicate message {MessageId}, skipping", messageId);
            await context.AcknowledgeAsync(ct);
            return;
        }

        try
        {
            await _inner.ProcessAsync(envelope, context, ct);
            await _inboxStore.MarkProcessedAsync(messageId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inbox: failed processing message {MessageId}", messageId);
            await _inboxStore.MarkFailedAsync(messageId, ex.Message, ct);
            throw;
        }
    }
}
