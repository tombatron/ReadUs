namespace ReadUs.Extras;

public abstract class Result<T> where T : notnull
{
    public static Result<T> Ok(T value) => new Ok<T>(value);
    
    public static Result<T> Error(string message) => new Error<T>(message);

    /// <summary>
    /// If you're sure that the instance of `Result<T>` is known to be OK, instead of having to handle `Ok` and `Error`
    /// you can invoke `{result}.Unwrap()` and it'll either return the value, or throw an exception.
    ///
    /// I'd try to keep the usage of this method to a minimum. 
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ResultUnwrapException"></exception>
    public T Unwrap() => this switch
    {
        Ok<T> ok => ok.Value,
        Error<T> error => throw new ResultUnwrapException($"[Error] [{this.GetType().Name}]: {error.Message}"),
        _ => throw new ResultUnwrapException("Not sure how you did, but congratulations, you're a programmer.")
    };
    
    /// <summary>
    /// You can use this method if you're sure that you won't have to handle the `Error` case, but gives you the option
    /// of providing a default value in the event that the `Result<T>` isn't an instance of `Ok<T>`.
    /// </summary>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    public T UnwrapOr(T defaultValue) => this is Ok<T> ok ? ok : defaultValue;
}