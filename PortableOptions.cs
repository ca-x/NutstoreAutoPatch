using System;

namespace NutstoreAutoPatch
{
    public sealed class PortableOptions
    {
        public bool ApplyUserData { get; set; }
        public bool DisableBackgroundUpdates { get; set; }
        public bool PortableLogs { get; set; }
        public bool DisableStartup { get; set; }

        public PortableOptions() { ApplyUserData = true; }

        public bool HasSelection
        {
            get { return ApplyUserData || DisableBackgroundUpdates || PortableLogs || DisableStartup; }
        }
    }

    internal sealed class PortableOperation
    {
        internal readonly string Name;
        internal readonly string Target;
        internal readonly Func<bool> Apply;
        internal readonly Action Restore;

        internal PortableOperation(string name, string target, Func<bool> apply, Action restore)
        {
            Name = name;
            Target = target;
            Apply = apply;
            Restore = restore;
        }
    }
}
