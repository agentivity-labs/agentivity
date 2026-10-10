namespace Agentivity.Domain.Common;

public sealed class OperationResult
{
    private OperationResult(bool isSuccess, string? error, object? payload)
    {
        IsSuccess = isSuccess;
        Error = error;
        Payload = payload;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? Error { get; }

    public object? Payload { get; }

    public static OperationResult Success(object? payload = null) => new(true, null, payload);

    public static OperationResult Failure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Error message is required", nameof(error));
        }

        return new OperationResult(false, error, null);
    }

    public OperationResult<T> As<T>()
    {
        if (Payload is T typed)
        {
            return OperationResult<T>.Success(typed);
        }

        return OperationResult<T>.Failure(Error ?? "Invalid payload type");
    }
}

public sealed class OperationResult<T>
{
    private OperationResult(bool isSuccess, T? value, string? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public string? Error { get; }

    public static OperationResult<T> Success(T value) => new(true, value, null);

    public static OperationResult<T> Failure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Error message is required", nameof(error));
        }

        return new OperationResult<T>(false, default, error);
    }
}
