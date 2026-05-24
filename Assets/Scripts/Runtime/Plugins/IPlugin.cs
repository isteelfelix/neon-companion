namespace NeonCompanion.Runtime.Plugins
{
    public interface IPlugin
    {
        string Id { get; }
        string Name { get; }
        string Version { get; }

        void OnInitialize(PluginContext context);
        void OnShutdown();
    }
}
