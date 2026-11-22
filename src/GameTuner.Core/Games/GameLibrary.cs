namespace GameTuner.Core.Games;

/// <summary>All games GameTuner knows how to configure.</summary>
public interface IGameLibrary
{
    IReadOnlyList<GameProfile> Profiles { get; }
    GameProfile? FindById(string id);
}

public sealed class GameLibrary : IGameLibrary
{
    public GameLibrary(IEnumerable<GameProfile> profiles)
    {
        Profiles = profiles.ToList();
    }

    public IReadOnlyList<GameProfile> Profiles { get; }

    public GameProfile? FindById(string id) =>
        Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
