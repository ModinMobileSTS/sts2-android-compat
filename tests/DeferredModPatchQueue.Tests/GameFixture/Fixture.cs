namespace Fixture
{
    public static class Probe
    {
        public static bool EssentialReady;
        public static bool UiTypeInitialized;
        public static bool ModelTypeInitialized;
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.DailyRun
{
    public static class NDailyRunScreen
    {
        static NDailyRunScreen()
        {
            Fixture.Probe.UiTypeInitialized = true;
            if (!Fixture.Probe.EssentialReady)
                throw new System.InvalidOperationException("NDailyRunScreen initialized before essential startup");
        }

        public static int SetupLobbyParams(int value) => value;
        public static int AllKinds(int value) => value;
        public static int Prepared(int value) => value;
        public static int Skipped(int value) => value;
        public static int Failing(int value) => value;
        public static int Direct(int value) => value;
    }
}

namespace MegaCrit.Sts2.Core.Models
{
    public static class SyntheticModel
    {
        static SyntheticModel() => Fixture.Probe.ModelTypeInitialized = true;
        public static int Register(int value) => value;
    }
}
