namespace GameProfileManager.Application;

public sealed record PcgamingWikiCredentials(string Username, string Password);

public interface IPcgamingWikiCredentialStore
{
    PcgamingWikiCredentials? Read();
    void Save(PcgamingWikiCredentials credentials);
    void Delete();
}
