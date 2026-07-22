using Smx.Domain.Intake;

namespace Smx.Domain.Tests;

public class AttachmentPathsTests
{
    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system.ini", "system.ini")]
    [InlineData("/absolute/path/report.pdf", "report.pdf")]
    [InlineData("normal name (1).pdf", "normal_name_1.pdf")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    public void SafeFilename_StripsEverythingThatCouldLeaveTheFolder(string input, string expected) =>
        Assert.Equal(expected, AttachmentPaths.SafeFilename(input));

    [Fact]
    public void Blob_PutsTheFileUnderItsOwnSessionAndFileId()
    {
        // The fileId segment is what keeps two uploads of "report.pdf" in one session from colliding.
        var path = AttachmentPaths.Blob("isx-aaaa1111", "att-bbbb2222", "report.pdf");
        Assert.Equal("intake/isx-aaaa1111/att-bbbb2222/report.pdf", path);
    }

    [Fact]
    public void Blob_CannotEscapeTheSessionFolder_EvenWithATraversingFilename()
    {
        // The filename arrives from a browser and is attacker-controlled in the general case. A path
        // that climbs out of intake/{sessionId}/ would let one session's upload overwrite another's.
        var path = AttachmentPaths.Blob("isx-aaaa1111", "att-bbbb2222", "../../other/evil.pdf");
        Assert.StartsWith("intake/isx-aaaa1111/att-bbbb2222/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IsASiblingOfTheFile_AndDoesNotDependOnTheFilename()
    {
        // Fixed name: the extracted text must be findable from (sessionId, fileId) alone, without
        // knowing what the original file was called.
        Assert.Equal("intake/isx-aaaa1111/att-bbbb2222/extracted.txt",
            AttachmentPaths.Text("isx-aaaa1111", "att-bbbb2222"));
    }

    [Theory]
    [InlineData("report.PDF", ".pdf")]
    [InlineData("data.tar.gz", ".gz")]
    [InlineData("noextension", "")]
    public void Extension_IsLowercasedAndTakenFromTheSanitisedName(string filename, string expected) =>
        Assert.Equal(expected, AttachmentPaths.Extension(filename));
}
