using System.Text.Json.Serialization;

namespace Momentum.Messaging.Outbox;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
