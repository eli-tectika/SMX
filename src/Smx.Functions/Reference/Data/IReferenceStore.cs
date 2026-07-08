// src/Smx.Functions/Reference/Data/IReferenceStore.cs
namespace Smx.Functions.Reference.Data;

public interface IReferenceStore
{
    Task UpsertAsync(string container, object doc, string partitionValue, CancellationToken ct);
}
