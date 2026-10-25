namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The spaces of the workspace: every object and every folder belongs to one, and an execution to the space of what it runs. Until
/// an edition offers several, there is one: <see cref="Default"/>, which holds everything (hidden where spaces are not offered).
/// </summary>
public static class WorkspaceSpaces
{
    /// <summary>The space everything is in until there are others.</summary>
    public const string Default = "default";

    /// <summary>
    /// The object types of the libraries, kept by space, by the name the API gives their kind. The credentials and the
    /// configurations are not among them: the whole instance shares them, available in the spaces an administrator says
    /// (<see cref="Identity.SpaceAvailability"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LibraryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["agent"] = "agent",
        ["team"] = "team",
        ["workflow"] = "workflow.definition",
        ["prompt"] = "prompt",
        ["datatable"] = "datatable",
        ["knowledgebase"] = "knowledge_base",
    };

    /// <summary>The name of the migration that put every object and folder in a space (the same for every engine).</summary>
    public const string MigrationName = "Every object and folder in a space";
}
