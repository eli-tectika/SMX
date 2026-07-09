using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Smx.Infrastructure;

/// <summary>
/// A Cosmos <see cref="CosmosSerializer"/> backed by System.Text.Json.
///
/// The Cosmos SDK's built-in serializer is Newtonsoft-based and cannot round-trip
/// <see cref="JsonElement"/>: on write it mangles it to <c>{"valueKind":…}</c> (losing the value),
/// and on read it throws when a change-feed item is requested as <c>JsonElement</c>. Both matter here
/// — <c>ProjectDoc.Payload</c> is a <see cref="JsonElement"/> and the change-feed processor reads
/// <c>ChangeFeedProcessorBuilder&lt;JsonElement&gt;</c>. Using STJ (the same options the domain uses)
/// makes the record store and the record-as-bus consistent end to end.
/// </summary>
public sealed class SystemTextJsonCosmosSerializer(JsonSerializerOptions options) : CosmosSerializer
{
    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            // The SDK sometimes asks for the raw stream back (e.g. for Stream-typed responses).
            if (typeof(Stream).IsAssignableFrom(typeof(T)))
                return (T)(object)stream;
            return JsonSerializer.Deserialize<T>(stream, options)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, options);
        stream.Position = 0;
        return stream;
    }
}
