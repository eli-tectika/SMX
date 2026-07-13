using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Smx.Infrastructure;

/// <summary>
/// A Cosmos serializer backed by System.Text.Json.
///
/// The Cosmos SDK's built-in serializer is Newtonsoft-based and cannot round-trip
/// <see cref="JsonElement"/>: on write it mangles it to <c>{"valueKind":…}</c> (losing the value),
/// and on read it throws when a change-feed item is requested as <c>JsonElement</c>. Both matter here
/// — <c>ProjectDoc.Payload</c> is a <see cref="JsonElement"/> and the change-feed processor reads
/// <c>ChangeFeedProcessorBuilder&lt;JsonElement&gt;</c>. Using STJ (the same options the domain uses)
/// makes the record store and the record-as-bus consistent end to end.
///
/// <para>
/// !! DO NOT re-base this class on <see cref="CosmosSerializer"/>. It MUST derive from
/// <see cref="CosmosLinqSerializer"/>. !!
/// </para>
///
/// <para>
/// The SDK's LINQ provider does not consult a <see cref="CosmosSerializer"/> for member naming. It takes
/// property names from <see cref="SerializeMemberName"/> — a member that exists only on
/// <see cref="CosmosLinqSerializer"/> — or from a <c>CosmosLinqSerializerOptions</c> passed explicitly to
/// each <c>GetItemLinqQueryable</c> call. With neither, it emits member names <b>exactly as declared in
/// C#</b>: PascalCase. Our documents are written camelCase (<c>Json.Options</c> is
/// <c>JsonSerializerDefaults.Web</c>), so a plain <see cref="CosmosSerializer"/> makes every LINQ query
/// address columns that exist on no stored document:
/// </para>
/// <code>
///   on disk:  {"id":"…","projectId":"p1","type":"verdict", …}
///   emitted:  SELECT VALUE root FROM root WHERE (root["Type"] = "verdict")   // matches nothing
/// </code>
///
/// <para>
/// The failure mode is the nastiest kind: <b>silent</b>. No exception — the queries just return an empty
/// list, forever, in Azure. And the test suite stays green while it happens, because the tests run against
/// <c>InMemoryRecordStore</c>, a dictionary that never generates SQL. This shipped once already and stalled
/// the whole pipeline: <c>GetVerdictsAsync</c> returning empty means <c>StageDispatcher.TryAssembleAsync</c>
/// can never satisfy <c>MatrixAssembler.IsComplete</c>, so the matrix never assembles and Regulatory never
/// parks in <c>awaiting-RE</c>; <c>search_catalog</c> returns nothing, so Discovery can propose no candidates.
/// </para>
///
/// <para>
/// <c>CosmosQueryTextTests</c> (in Smx.Orchestrator.Tests) is the guard: it asserts on the SQL text the SDK
/// actually emits. If you change the base class, that is the test that will fail. Believe it.
/// </para>
/// </summary>
public sealed class SystemTextJsonCosmosSerializer(JsonSerializerOptions options) : CosmosLinqSerializer
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

    /// <summary>
    /// Tells the LINQ provider what a member is called <i>on the wire</i>. This must reproduce exactly what
    /// <see cref="JsonSerializer"/> does with <paramref name="memberInfo"/> under the same
    /// <see cref="JsonSerializerOptions"/> — if the two ever disagree, the query and the document go back to
    /// naming different columns, just more subtly than before.
    /// </summary>
    public override string? SerializeMemberName(MemberInfo memberInfo)
    {
        // 1. An explicit [JsonPropertyName] wins over everything, naming policy included.
        if (memberInfo.GetCustomAttribute<JsonPropertyNameAttribute>() is { } name)
            return name.Name;

        // 2. An unconditionally-ignored member is on no document, so it can be on no query.
        //    The SDK reads null as "not serialized". Note the Condition check: STJ still writes members
        //    marked [JsonIgnore(Condition = WhenWritingNull/WhenWritingDefault)], so those stay queryable.
        if (memberInfo.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
            return null;

        // 3. Otherwise the naming policy decides (camelCase, via JsonSerializerDefaults.Web).
        return options.PropertyNamingPolicy?.ConvertName(memberInfo.Name) ?? memberInfo.Name;
    }
}
