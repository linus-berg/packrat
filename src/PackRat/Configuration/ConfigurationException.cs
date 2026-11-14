namespace PackRat.Configuration;

/// <summary>
///   Invalid or missing configuration. Exits with EX_CONFIG (78).
/// </summary>
public sealed class ConfigurationException : Exception {
  public ConfigurationException(string message) : base(message) { }

  public ConfigurationException(string message, Exception inner)
    : base(message, inner) { }
}
