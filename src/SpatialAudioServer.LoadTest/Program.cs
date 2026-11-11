namespace SpatialAudioServer.LoadTest;

/// <summary>
/// Load generator (Phase 2, later stage): runs N fake clients against a server over HTTP.
/// For now the FakeClient/ToneMeter classes here are used by the integration tests.
/// </summary>
/// <remarks>Explicit entry point: a top-level-statements Program would clash with the server's Program in the tests.</remarks>
public static class LoadTestProgram
{
    public static int Main()
    {
        Console.Error.WriteLine("SpatialAudioServer.LoadTest: load runs are not implemented yet.");
        return 2;
    }
}
