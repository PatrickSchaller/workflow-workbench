using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace WorkflowWorkbench;

// Explicit in-memory storage also keeps framework key-ring warm-up away from the user's shared key directory.
internal sealed class MemoryKeyRepository : IXmlRepository
{
    private readonly ConcurrentQueue<XElement> elements = new();
    public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
    public void StoreElement(XElement element, string friendlyName) => elements.Enqueue(new XElement(element));
}
