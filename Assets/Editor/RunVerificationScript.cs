#if UNITY_EDITOR
using UnityEditor;

namespace Scorched.Editor
{
    public static class RunVerificationScript
    {
        public static string Execute()
        {
            TestShadeDurabilityVerification.RunVerification();
            return "SUCCESS";
        }
    }
}
#endif

