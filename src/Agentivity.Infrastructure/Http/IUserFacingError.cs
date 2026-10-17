namespace Agentivity.Infrastructure.Http;

/// <summary>
/// An error that knows what to tell the person: the answer the API gives for it (status, code) and the words, ones they can act
/// on. An exception that implements it is answered with these by the API's single exception handler, instead of "An unexpected
/// error occurred.". Its <see cref="Exception.Message"/> stays the technical detail.
/// </summary>
public interface IUserFacingError
{
    int HttpStatus { get; }

    string Code { get; }

    string UserMessage { get; }
}
