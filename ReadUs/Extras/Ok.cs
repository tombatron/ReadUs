namespace ReadUs.Extras;

public sealed class Ok<T>(T value) : Result<T> where T : notnull
{
    public T Value => value;
    
    public static implicit operator T(Ok<T> ok) => ok.Value;
}