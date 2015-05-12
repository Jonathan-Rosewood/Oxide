﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

using ObjectStream;
using ObjectStream.Data;

using Oxide.Core;

namespace Oxide.Plugins
{
    public class PluginCompiler
    {
        public static string BinaryPath;

        public static void CheckCompilerBinary()
        {
            BinaryPath = null;
            var root_directory = Interface.Oxide.RootDirectory;
            var binary_path = root_directory + @"\basic.exe";
            if (File.Exists(binary_path))
            {
                BinaryPath = binary_path;
                return;
            }
            if (!File.Exists(root_directory + @"\monosgen-2.0.dll"))
            {
                Interface.Oxide.LogError("Cannot compile C# plugins. Unable to find monosgen-2.0.dll!");
                return;
            }
            if (!File.Exists(root_directory + @"\msvcr120.dll") && !File.Exists(Environment.SystemDirectory + @"\msvcr120.dll"))
            {
                Interface.Oxide.LogError("Cannot compile C# plugins. Unable to find msvcr120.dll!");
                return;
            }
            binary_path = root_directory + @"\CSharpCompiler.exe";
            if (!File.Exists(binary_path))
            {
                Interface.Oxide.LogError("Cannot compile C# plugins. Unable to find CSharpCompiler.exe!");
                return;
            }
            BinaryPath = binary_path;
        }
        
        private Process process;
        private Regex fileErrorRegex = new Regex(@"([\w\.]+)\(\d+,\d+\): error|error \w+: Source file `[\\\./]*([\w\.]+)", RegexOptions.Compiled);
        private ObjectStreamClient<CompilerMessage> client;
        private Dictionary<int, Compilation> pluginComp;
        private Queue<CompilerMessage> compQueue;
        private volatile int lastId;
        private volatile bool ready;

        class Compilation
        {
            public Action<byte[], float> callback;
            public List<CompilablePlugin> plugins;
            public float startedAt;
            public float endedAt;
            public string compiledName;
            public HashSet<string> references;
            public float Duration
            {
                get { return endedAt - startedAt; }
            }
        }

        public PluginCompiler()
        {
            CheckCompilerBinary();
            if (BinaryPath == null) return;

            pluginComp = new Dictionary<int, Compilation>();
            compQueue = new Queue<CompilerMessage>();

            process = new Process
            {
                StartInfo =
                {
                    FileName = BinaryPath,
                    Arguments = "/service",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                }
            };
            process.Start();
            client = new ObjectStreamClient<CompilerMessage>(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            client.Message += OnMessage;
            client.Error += OnError;
            client.Start();
        }

        public void ResolveReferences(int currentId, Action callback)
        {
            var compilation = pluginComp[currentId];
            // Include references made by the CSharpPlugins project
            compilation.references = new HashSet<string>(CSharpPluginLoader.PluginReferences);

            ThreadPool.QueueUserWorkItem((_) =>
            {
                try
                {
                    CacheAllScripts(compilation.plugins);

                    var extension_names = Interface.Oxide.GetAllExtensions().Select(ext => ext.Name).ToArray();
                    var include_path = Interface.Oxide.PluginDirectory + "\\Include";

                    foreach (var plugin in compilation.plugins.ToArray())
                    {
                        plugin.References.Clear();
                        plugin.IncludePaths.Clear();

                        bool parsingNamespace = false;
                        foreach (var script_line in plugin.ScriptLines)
                        {
                            var line = script_line.Trim();
                            if (line.Length < 1) continue;

                            Match match;
                            if (parsingNamespace)
                            {
                                // Skip blank lines and opening brace at the top of the namespace block
                                match = Regex.Match(line, @"^\s*\{?\s*$", RegexOptions.IgnoreCase);
                                if (match.Success) continue;

                                // Skip class custom attributes
                                match = Regex.Match(line, @"^\s*\[", RegexOptions.IgnoreCase);
                                if (match.Success) continue;

                                // Detect main plugin class name
                                match = Regex.Match(line, @"^\s*(?:public |private |protected )?class\s+(\S+)\s+\:\s+\S+Plugin\s*$", RegexOptions.IgnoreCase);
                                if (!match.Success) break;

                                var class_name = match.Groups[1].Value;
                                if (class_name != plugin.Name)
                                {
                                    Interface.Oxide.LogError("Plugin filename is incorrect: {0}.cs", plugin.ScriptName);
                                    plugin.CompilerErrors = "Plugin filename is incorrect";
                                    RemovePlugin(compilation.plugins, plugin);
                                }

                                break;
                            }
                            else
                            {
                                // Include explicit references defined by magic comments in script
                                match = Regex.Match(line, @"^//\s?Reference:\s?(\S+)$", RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    AddReference(currentId, plugin, match.Groups[1].Value);
                                    continue;
                                }

                                // Include implicit references detected from using statements in script
                                match = Regex.Match(line, @"^\s*using\s+([^;]+)\s*;$", RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    var split_name = match.Groups[1].Value.Trim().Split('.');
                                    if (split_name.Length > 2 && split_name[0] == "Oxide" && split_name[1] == "Ext")
                                        AddReference(currentId, plugin, "Oxide.Ext." + split_name[2]);
                                    continue;
                                }

                                // Start parsing the Oxide.Plugins namespace contents
                                match = Regex.Match(line, @"^\s*namespace Oxide\.Plugins\s*(\{\s*)?$", RegexOptions.IgnoreCase);
                                if (match.Success) parsingNamespace = true;
                            }
                        }

                        if (!Directory.Exists(include_path)) continue;
                        
                        foreach (var reference in plugin.References)
                        {
                            if (!reference.StartsWith("Oxide.Ext.")) continue;
                            var name = reference.Substring(10);
                            if (extension_names.Contains(name)) continue;
                            var include_file_path = include_path + "\\Ext." + name + ".cs";
                            if (File.Exists(include_file_path))
                            {
                                plugin.IncludePaths.Add(include_file_path);
                                continue;
                            }
                            var message = $"{name} extension is referenced but is not loaded! An appropriate include file needs to be saved to Plugins\\Include\\Ext.{name}.cs if this is an optional dependency.";
                            Interface.Oxide.LogError(message);
                            plugin.CompilerErrors = message;
                            RemovePlugin(compilation.plugins, plugin);
                        }
                    }

                    callback();
                }
                catch (Exception ex)
                {
                    Interface.Oxide.LogException("Exception while resolving plugin references", ex);
                    RemoteLogger.Exception("Exception while resolving plugin references", ex);
                }
            });
        }

        private void AddReference(int currentId, CompilablePlugin plugin, string assembly_name)
        {
            var compilation = pluginComp[currentId];
            var path = string.Format("{0}\\{1}.dll", Interface.Oxide.ExtensionDirectory, assembly_name);
            if (!File.Exists(path))
            {
                if (assembly_name.StartsWith("Oxide.Ext."))
                {
                    plugin.References.Add(assembly_name);
                    return;
                }
                Interface.Oxide.LogError("Assembly referenced by {0} plugin does not exist: {1}.dll", plugin.Name, assembly_name);
                plugin.CompilerErrors = "Referenced assembly does not exist: " + assembly_name;
                RemovePlugin(compilation.plugins, plugin);
                return;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(assembly_name);
            }
            catch (FileNotFoundException)
            {
                Interface.Oxide.LogError("Assembly referenced by {0} plugin is invalid: {1}.dll", plugin.Name, assembly_name);
                plugin.CompilerErrors = "Referenced assembly is invalid: " + assembly_name;
                RemovePlugin(compilation.plugins, plugin);
                return;
            }

            compilation.references.Add(assembly_name);
            plugin.References.Add(assembly_name);

            // Include references made by the referenced assembly
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                compilation.references.Add(reference.Name);
                plugin.References.Add(reference.Name);
            }
        }

        public void Compile(List<CompilablePlugin> plugins, Action<byte[], float> callback)
        {
            if (BinaryPath == null) return;
            var currentId = lastId++;
            pluginComp[currentId] = new Compilation {callback = callback, plugins = plugins};

            ResolveReferences(currentId, () =>
            {
                if (plugins.Count < 1) return;
                foreach (var plugin in plugins) plugin.CompilerErrors = null;
                SpawnCompiler(currentId);
            });
        }

        private void SpawnCompiler(int currentId)
        {
            var compilation = pluginComp[currentId];
            compilation.startedAt = Interface.Oxide.Now;
            var referenceFiles = new List<CompilerFile>(compilation.references.Count);
            referenceFiles.AddRange(compilation.references.Select(reference_name => new CompilerFile { Name = reference_name + ".dll", Data = File.ReadAllBytes(Path.Combine(Interface.Oxide.ExtensionDirectory, reference_name + ".dll")) }));

            var sourceFiles = compilation.plugins.SelectMany(plugin => plugin.IncludePaths).Select(includePath => new CompilerFile { Name = Path.GetFileName(includePath), Data = File.ReadAllBytes(includePath) }).ToList();
            sourceFiles.AddRange(compilation.plugins.Select(plugin => plugin.ScriptPath).Select(scriptPath => new CompilerFile { Name = Path.GetFileName(scriptPath), Data = File.ReadAllBytes(scriptPath) }));

            var compilerData = new CompilerData
            {
                OutputFile = (compilation.plugins.Count == 1 ? compilation.plugins[0].Name : "plugins_") + Math.Round(Interface.Oxide.Now * 10000000f) + ".dll",
                SourceFiles = sourceFiles.ToArray(),
                ReferenceFiles = referenceFiles.ToArray()
            };
            var message = new CompilerMessage {Id = currentId, Data = compilerData, Type = CompilerMessageType.Compile};
            if (ready)
                client.PushMessage(message);
            else
                compQueue.Enqueue(message);
        }

        private void OnError(Exception exception)
        {
            Interface.Oxide.LogException("Compilation error: ", exception);
        }

        private void OnMessage(ObjectStreamConnection<CompilerMessage, CompilerMessage> connection, CompilerMessage message)
        {
            switch (message.Type)
            {
                case CompilerMessageType.Assembly:
                    var compilation = pluginComp[message.Id];
                    compilation.endedAt = Interface.Oxide.Now;
                    var stdOutput = (string)message.ExtraData;
                    if (stdOutput != null)
                    {
                        foreach (var line in stdOutput.Split('\r', '\n'))
                        {
                            var match = fileErrorRegex.Match(line.Trim());
                            for (var i = 1; i < match.Groups.Count; i++)
                            {
                                var value = match.Groups[i].Value;
                                if (value.Trim() == string.Empty) continue;
                                var file_name = value.Basename();
                                var script_name = file_name.Substring(0, file_name.Length - 3);
                                var compilable_plugin = compilation.plugins.SingleOrDefault(pl => pl.ScriptName == script_name);
                                if (compilable_plugin == null)
                                    Interface.Oxide.LogError("Unable to resolve script error to plugin: " + line);
                                else
                                    compilable_plugin.CompilerErrors = line.Trim().Replace(Interface.Oxide.PluginDirectory + "\\", string.Empty);
                            }
                        }
                    }
                    Interface.Oxide.NextTick(() => compilation.callback((byte[])message.Data, compilation.Duration));
                    pluginComp.Remove(message.Id);
                    break;
                case CompilerMessageType.Error:
                    Interface.Oxide.LogError("Compilation error: {0}", message.Data);
                    var comp = pluginComp[message.Id];
                    Interface.Oxide.NextTick(() => comp.callback(null, 0));
                    pluginComp.Remove(message.Id);
                    break;
                case CompilerMessageType.Ready:
                    connection.PushMessage(message);
                    if (!ready)
                    {
                        ready = true;
                        while (compQueue.Count > 0)
                            connection.PushMessage(compQueue.Dequeue());
                    }
                    break;
            }
        }

        private bool CacheScriptLines(CompilablePlugin plugin)
        {
            var waiting_for_access = false;
            while (true)
            {
                try
                {
                    if (!File.Exists(plugin.ScriptPath))
                    {
                        Interface.Oxide.LogWarning("Script no longer exists: {0}", plugin.Name);
                        plugin.CompilerErrors = "Plugin file was deleted";
                        return false;
                    }
                    plugin.ScriptLines = File.ReadAllLines(plugin.ScriptPath);
                    return true;
                }
                catch (IOException)
                {
                    if (!waiting_for_access)
                    {
                        waiting_for_access = true;
                        Interface.Oxide.LogWarning("Waiting for another application to stop using script: {0}", plugin.Name);
                    }
                    Thread.Sleep(50);
                }
            }
        }

        private void CacheModifiedScripts(List<CompilablePlugin> plugins)
        {
            Thread.Sleep(100);
            var modified_plugins = plugins.Where(pl => pl.HasBeenModified()).ToArray();
            if (modified_plugins.Length < 1) return;
            foreach (var plugin in modified_plugins)
                CacheScriptLines(plugin);
            CacheModifiedScripts(plugins);
        }

        private void CacheAllScripts(List<CompilablePlugin> plugins)
        {
            foreach (var plugin in plugins.ToArray())
                if (!CacheScriptLines(plugin)) RemovePlugin(plugins, plugin);
            CacheModifiedScripts(plugins);
        }

        private void RemovePlugin(List<CompilablePlugin> plugins, CompilablePlugin plugin)
        {
            plugins.Remove(plugin);
            plugin.OnCompilationFailed();
        }

        public void OnShutdown()
        {
            if (client != null)
            {
                client.PushMessage(new CompilerMessage { Type = CompilerMessageType.Exit });
                client.Stop();
            }
        }
    }
}
