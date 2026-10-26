namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What a person may do now, outside any request of theirs (something they set up, done later in their name): null when they may
/// do nothing any more (no longer a person of the instance, or deactivated). Without sign-in, there is none: the owner does all.
/// </summary>
public interface ICallerRights
{
    Task<SpaceAccess?> AccessOfAsync(Caller caller, CancellationToken cancellationToken);
}
