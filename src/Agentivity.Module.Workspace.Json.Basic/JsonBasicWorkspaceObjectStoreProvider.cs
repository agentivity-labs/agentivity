using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.Json.Basic;

public sealed class JsonBasicWorkspaceObjectStoreProvider(ILoggerFactory loggerFactory) : IWorkspaceObjectStoreProvider
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public IWorkspaceObjectStore CreateStore(string filePath) => new JsonBasicWorkspaceObjectStore(filePath, _loggerFactory.CreateLogger<JsonBasicWorkspaceObjectStore>());
}
