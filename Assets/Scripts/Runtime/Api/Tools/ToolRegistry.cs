using System.Collections.Generic;
using NeonCompanion.Runtime.Api.Models;

namespace NeonCompanion.Runtime.Api.Tools
{
    internal static class ToolRegistry
    {
        public static List<ToolDefinition> GetToolDefinitions()
        {
            var tools = new List<ToolDefinition>();

            var readFile = new ToolDefinition();
            readFile.name = "read_file";
            readFile.description = "Read the contents of a file at the given path";
            readFile.parameters = new ToolParameterSchema();
            readFile.parameters.properties = new Dictionary<string, ToolParameterProperty>();
            readFile.parameters.properties["path"] = new ToolParameterProperty { type = "string", description = "Absolute file path" };
            readFile.parameters.required = new List<string> { "path" };
            tools.Add(readFile);

            var writeFile = new ToolDefinition();
            writeFile.name = "write_file";
            writeFile.description = "Write content to a file at the given path. Creates the file if it doesn't exist.";
            writeFile.parameters = new ToolParameterSchema();
            writeFile.parameters.properties = new Dictionary<string, ToolParameterProperty>();
            writeFile.parameters.properties["path"] = new ToolParameterProperty { type = "string", description = "Absolute file path" };
            writeFile.parameters.properties["content"] = new ToolParameterProperty { type = "string", description = "File content to write" };
            writeFile.parameters.required = new List<string> { "path", "content" };
            tools.Add(writeFile);

            var listFiles = new ToolDefinition();
            listFiles.name = "list_files";
            listFiles.description = "List files and directories at the given path";
            listFiles.parameters = new ToolParameterSchema();
            listFiles.parameters.properties = new Dictionary<string, ToolParameterProperty>();
            listFiles.parameters.properties["path"] = new ToolParameterProperty { type = "string", description = "Directory path" };
            listFiles.parameters.required = new List<string> { "path" };
            tools.Add(listFiles);

            var searchFiles = new ToolDefinition();
            searchFiles.name = "search_files";
            searchFiles.description = "Search for text in files within a directory";
            searchFiles.parameters = new ToolParameterSchema();
            searchFiles.parameters.properties = new Dictionary<string, ToolParameterProperty>();
            searchFiles.parameters.properties["path"] = new ToolParameterProperty { type = "string", description = "Directory to search in" };
            searchFiles.parameters.properties["pattern"] = new ToolParameterProperty { type = "string", description = "Text or regex pattern to search for" };
            searchFiles.parameters.required = new List<string> { "path", "pattern" };
            tools.Add(searchFiles);

            var execCode = new ToolDefinition();
            execCode.name = "execute_code";
            execCode.description = "Execute a Python script and return its output";
            execCode.parameters = new ToolParameterSchema();
            execCode.parameters.properties = new Dictionary<string, ToolParameterProperty>();
            execCode.parameters.properties["code"] = new ToolParameterProperty { type = "string", description = "Python code to execute" };
            execCode.parameters.required = new List<string> { "code" };
            tools.Add(execCode);

            return tools;
        }
    }
}
