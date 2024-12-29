namespace ReadUs.Extras;

public sealed class Error<T>(string message) : Result<T> where T : notnull
{
    public string Message => message;
}