[assembly: WixToolset.Mba.Core.BootstrapperApplicationFactory(typeof(BlueLink.SetupUI.BlueLinkBootstrapperFactory))]

namespace BlueLink.SetupUI
{
    using WixToolset.Mba.Core;

    public sealed class BlueLinkBootstrapperFactory : BaseBootstrapperApplicationFactory
    {
        protected override IBootstrapperApplication Create(IEngine engine, IBootstrapperCommand command)
        {
            return new BlueLinkBootstrapper(engine, command);
        }
    }
}
