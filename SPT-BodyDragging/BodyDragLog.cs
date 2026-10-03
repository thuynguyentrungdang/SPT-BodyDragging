namespace BodyDragging
{
    internal static class BodyDragLog
    {
        private static bool IsDebugEnabled => Plugin.DebugLogging != null && Plugin.DebugLogging.Value;

        internal static void Info(object message)
        {
            if (IsDebugEnabled)
                Plugin.Log?.LogInfo(message);
        }

        internal static void Warning(object message)
        {
            Plugin.Log?.LogWarning(message);
        }

        internal static void Error(object message)
        {
            Plugin.Log?.LogError(message);
        }
    }
}
