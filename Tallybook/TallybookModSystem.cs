using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Tallybook
{
    /// <summary>
    /// Client-side crafting shopping list. See tallybook-mod-spec.md.
    ///
    /// Client-only by construction: the ShouldLoad gate below and "side": "Client" in
    /// modinfo.json are both load-bearing. A dedicated server still unpacks the zip and
    /// loads this assembly, but must never see a single line of Tallybook output — that
    /// silence is pinned as a regression invariant in tools/compat-test.ps1.
    /// </summary>
    public class TallybookModSystem : ModSystem
    {
        ICoreClientAPI capi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
        }
    }
}
