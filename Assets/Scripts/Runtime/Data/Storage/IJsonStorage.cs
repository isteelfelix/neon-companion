namespace NeonCompanion.Runtime.Data.Storage
{
    public interface IJsonStorage
    {
        T Load<T>(string path) where T : new();
        void Save<T>(string path, T data);
    }
}
