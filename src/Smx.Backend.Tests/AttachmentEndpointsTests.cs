using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class AttachmentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AttachmentEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IIntakeSessionStore sessions, IAttachmentBlobStore blobs) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(sessions);
            s.AddSingleton(blobs);
        }));

    private static async Task<(string id, InMemoryIntakeSessionStore sessions)> SeededAsync()
    {
        var sessions = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-22T10:00:00.0000000Z",
        });
        return (id, sessions);
    }

    /// Named FilePart, not File: a static member called `File` would shadow `System.IO.File` for the
    /// whole class. The form field name MUST be "file" — it is what binds to the handler's `IFormFile file`.
    private static MultipartFormDataContent FilePart(string filename, string content, string contentType)
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", filename } };
    }

    [Fact]
    public async Task Upload_StoresTheFile_ExtractsIt_AndRecordsItOnTheSession()
    {
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("notes.txt", "the adhesive is water-based", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var attachment = Assert.Single((await sessions.GetAsync(id))!.Attachments);
        Assert.Equal(AttachmentStatus.Extracted, attachment.Status);
        Assert.Equal("notes.txt", attachment.Filename);
        // Both blobs: the original bytes AND the extracted text.
        Assert.True(blobs.Exists(attachment.BlobPath));
        Assert.Equal("the adhesive is water-based", await blobs.GetTextAsync(attachment.TextBlobPath!));
    }

    [Fact]
    public async Task Upload_RecordsAnUnreadableFile_RatherThanRejectingIt()
    {
        // THE point of design §5.2. A file we cannot parse must still be stored and still appear in the
        // agent's context by name and status, so the agent asks the operator what it shows. Refusing
        // the upload would make the file — and the fact that it exists — invisible.
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("line-photo.jpg", "not text, and no extractor claims .jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var attachment = Assert.Single((await sessions.GetAsync(id))!.Attachments);
        Assert.Equal(AttachmentStatus.Unsupported, attachment.Status);
        Assert.False(string.IsNullOrWhiteSpace(attachment.Error));
        Assert.True(blobs.Exists(attachment.BlobPath), "the bytes must be kept even when unreadable");
        Assert.Null(attachment.TextBlobPath);
    }

    [Fact]
    public async Task Upload_IsA404ForAnUnknownSession()
    {
        using var app = NewApp(new InMemoryIntakeSessionStore(), new InMemoryAttachmentBlobStore());
        var res = await app.CreateClient().PostAsync("/intake-sessions/isx-nope/attachments",
            FilePart("notes.txt", "hi", "text/plain"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Upload_RefusesOnceTheSessionIsFull()
    {
        // Server-side, not a browser courtesy: the cap exists so the session document's attachment
        // metadata cannot grow past Cosmos's 2 MB item limit.
        var (id, sessions) = await SeededAsync();
        var session = (await sessions.GetAsync(id))!;
        for (var i = 0; i < AttachmentLimits.MaxFilesPerSession; i++)
            session.Attachments.Add(new SessionAttachment
            {
                FileId = $"att-{i:D4}", Filename = $"f{i}.txt", Status = AttachmentStatus.Extracted,
            });
        await sessions.UpsertAsync(session);
        using var app = NewApp(sessions, new InMemoryAttachmentBlobStore());

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("one-too-many.txt", "hi", "text/plain"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(AttachmentLimits.MaxFilesPerSession, (await sessions.GetAsync(id))!.Attachments.Count);
    }

    [Fact]
    public async Task Upload_KeepsTwoFilesOfTheSameNameApart()
    {
        // The operator re-uploads a corrected "questionnaire.pdf". The second must not overwrite the
        // first's blob, or the agent's earlier citation now points at different content.
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);
        var client = app.CreateClient();

        await client.PostAsync($"/intake-sessions/{id}/attachments", FilePart("q.txt", "first", "text/plain"));
        await client.PostAsync($"/intake-sessions/{id}/attachments", FilePart("q.txt", "second", "text/plain"));

        var attachments = (await sessions.GetAsync(id))!.Attachments;
        Assert.Equal(2, attachments.Count);
        Assert.NotEqual(attachments[0].BlobPath, attachments[1].BlobPath);
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheAttachmentSurface()
    {
        // Trap 1's regression guard: a missing [FromServices] on any store parameter breaks routing for
        // the WHOLE app, and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryIntakeSessionStore(), new InMemoryAttachmentBlobStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
