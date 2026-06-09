using System;
using System.Reflection;
using System.Linq;

class Program
{
    static void Main()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = Microsoft.Extensions.DependencyInjection.McpServerServiceCollectionExtensions.AddMcpServer(services);

        var contentBlockType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.FullName == "ModelContextProtocol.Protocol.ContentBlock");

        if (contentBlockType == null)
        {
            Console.WriteLine("ContentBlock not found.");
            return;
        }

        Console.WriteLine($"ContentBlock is Abstract: {contentBlockType.IsAbstract}");
        
        var derivedTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsSubclassOf(contentBlockType))
            .ToList();

        foreach (var dt in derivedTypes)
        {
            Console.WriteLine($"Derived: {dt.FullName}");
            foreach (var p in dt.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Console.WriteLine($"  Property: {p.Name} -> {p.PropertyType}");
            }
            foreach (var c in dt.GetConstructors())
            {
                Console.WriteLine($"  Constructor: {c}");
            }
        }
    }
}
