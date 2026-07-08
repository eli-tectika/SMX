namespace Smx.Domain.Records;

public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
}

public static class Stages
{
    public const string Intake = "intake";
    public const string Screening = "screening";
    public const string Matrix = "matrix";
}

public static class RecordIds
{
    public static string Constraints(string projectId) => $"{projectId}|constraints";
    public static string Verdict(string projectId, string cas, string componentId) => $"{projectId}|verdict|{cas}|{componentId}";
    public static string Matrix(string projectId) => $"{projectId}|matrix";
}
