namespace Nomori.Marketplace.Core.Configuration;

/// <summary>
/// Marks a typed configuration object used by the application.
/// </summary>
public interface IConfig
{
    string Name => GetType().Name;

    int Order => 1;
}