using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public interface IChatSessionRepository
    {
        List<ChatSession> GetAll();
        void SaveAll(List<ChatSession> sessions);
    }
}
