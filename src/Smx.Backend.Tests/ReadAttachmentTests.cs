using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Agents;

namespace Smx.Backend.Tests;

public class ReadAttachmentTests
{
    private static async Task<(InterviewTools tools, InMemoryIntakeSessionStore sessions,
        InMemoryAttachmentBlobStore blobs, string id)> SetupAsync(params SessionAttachment[] attachments)
    {
        var sessions = new InMemoryIntakeSessionStore();
        var blobs = new InMemoryAttachmentBlobStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Attachments = [.. attachments],
        });
        return (new InterviewTools(sessions, new InMemoryRecordStore(), blobs, id), sessions, blobs, id);
    }

    private static AIFunction Tool(InterviewTools tools, string name) =>
        tools.Tools().OfType<AIFunction>().Single(f => f.Name == name);

    private static Task<object?> InvokeAsync(AIFunction fn, object args) =>
        fn.InvokeAsync(new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(args))!), default).AsTask();

    private static SessionAttachment Extracted(string fileId, string filename, string textPath) => new()
    {
        FileId = fileId, Filename = filename, Status = AttachmentStatus.Extracted,
        BlobPath = $"intake/s/{fileId}/{filename}", TextBlobPath = textPath,
    };

    [Fact]
    public async Task ReadAttachment_ReturnsTheExtractedText()
    {
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-1111", "notes.txt", "intake/s/att-1111/extracted.txt"));
        await blobs.PutTextAsync("intake/s/att-1111/extracted.txt", "the adhesive is water-based");

        var result = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-1111" }))?.ToString();

        Assert.Contains("water-based", result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachment_ResolvesThroughTheSession_SoAForgedIdCannotReachAnotherFile()
    {
        // THE safety property of this tool. The fileId comes from a LANGUAGE MODEL. If it were
        // interpolated into a blob path, a hallucinated or crafted value could read another interview's
        // upload — or anything else in `bronze`, which also holds the SDS corpus. The path used is the
        // one STORED on the session's own attachment list; an id that is not in that list is refused
        // without any blob being touched.
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-1111", "notes.txt", "intake/s/att-1111/extracted.txt"));
        await blobs.PutTextAsync("intake/other-session/att-9999/extracted.txt", "SOMEONE ELSE'S PROJECT");

        var result = (await InvokeAsync(Tool(tools, "read_attachment"),
            new { fileId = "../other-session/att-9999" }))?.ToString();

        Assert.DoesNotContain("SOMEONE ELSE", result!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("att-1111", result!, StringComparison.Ordinal);   // it lists what IS available
    }

    [Fact]
    public async Task ReadAttachment_SaysWhyItCannotReadAnUnsupportedFile()
    {
        // The agent must be able to tell "there is nothing in this file" from "I cannot open this
        // file" — only the second is a reason to ask the operator what it shows.
        var (tools, _, _, _) = await SetupAsync(new SessionAttachment
        {
            FileId = "att-2222", Filename = "line-photo.jpg", Status = AttachmentStatus.Unsupported,
            Error = "there is no extractor for .jpg files",
        });

        var result = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-2222" }))?.ToString();

        Assert.Contains("line-photo.jpg", result!, StringComparison.Ordinal);
        Assert.Contains("ask the operator", result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAttachment_PagesALongDocument_AndSaysThereIsMore()
    {
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-3333", "big.txt", "intake/s/att-3333/extracted.txt"));
        await blobs.PutTextAsync("intake/s/att-3333/extracted.txt",
            new string('a', AttachmentLimits.PageChars) + new string('b', 500));

        var page1 = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-3333" }))?.ToString();
        var page2 = (await InvokeAsync(Tool(tools, "read_attachment"),
            new { fileId = "att-3333", page = 2 }))?.ToString();

        Assert.Contains("page 1 of 2", page1!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bbb", page1!, StringComparison.Ordinal);
        Assert.Contains("bbb", page2!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentSchema_DoesNotRequireThePage_SoAOneArgCallBinds()
    {
        // Trap: AIFunctionFactory emits a parameter WITHOUT a default as `required` regardless of the
        // description, and the binder then rejects every ordinary one-argument call before the body
        // runs. This is exactly how a tool ships dead on arrival.
        var (tools, _, _, _) = await SetupAsync();
        var schema = Tool(tools, "read_attachment").JsonSchema.ToString();

        Assert.Contains("fileId", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\"", RequiredSectionOf(schema), StringComparison.Ordinal);
    }

    /// The `required` array of the tool's JSON schema, or "" when there is none.
    private static string RequiredSectionOf(string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.TryGetProperty("required", out var req) ? req.ToString() : "";
    }

    [Fact]
    public void RenderAttachments_NamesAnUnreadableFileWithItsStatus()
    {
        // Design §5.2: an unreadable file is a VISIBLE FACT, never silence. If it did not reach the
        // agent's context the operator would never be asked what it shows, and the analysis would run
        // as though the file said nothing.
        var session = new IntakeSessionDoc
        {
            Id = "isx-1", SessionId = "isx-1", CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Attachments =
            [
                new() { FileId = "att-2222", Filename = "line-photo.jpg",
                        ContentType = "image/jpeg", Status = AttachmentStatus.Unsupported },
            ],
        };

        var rendered = InterviewAgent.RenderAttachments(session);

        Assert.Contains("line-photo.jpg", rendered, StringComparison.Ordinal);
        Assert.Contains("CANNOT", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
