using System.Collections.Generic;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Storage;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public sealed class ChatSessionRepository : IChatSessionRepository
    {
        private readonly IJsonStorage _storage;

        public ChatSessionRepository(IJsonStorage storage)
        {
            _storage = storage;
        }

        public List<ChatSession> GetAll()
        {
            var collection = _storage.Load<ChatSessionCollection>(AppPaths.SessionsFile);
            return collection.items ?? new List<ChatSession>();
        }

        public void SaveAll(List<ChatSession> sessions)
        {
            _storage.Save(AppPaths.SessionsFile, new ChatSessionCollection
            {
                items = sessions ?? new List<ChatSession>()
            });
        }
    }
}
