﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Neo.IronLua;

namespace CitizenMP.Server.Resources
{
    class ScriptEnvironment : IDisposable
    {
        private Resource m_resource;
        private LuaGlobal m_luaEnvironment;

        private static Lua ms_luaState;
        private static ILuaDebug ms_luaDebug;
        private static List<KeyValuePair<string, MethodInfo>> ms_luaFunctions = new List<KeyValuePair<string, MethodInfo>>();
        //private static List<KeyValuePair<string, LuaNativeFunction>> ms_nativeFunctions = new List<KeyValuePair<string, LuaNativeFunction>>();

        [ThreadStatic]
        private static ScriptEnvironment ms_currentEnvironment;

        public static ScriptEnvironment CurrentEnvironment
        {
            get
            {
                return ms_currentEnvironment;
            }
        }

        [ThreadStatic]
        private static ScriptEnvironment ms_lastEnvironment;

        [ThreadStatic]
        private static int refCount;

        public static ScriptEnvironment LastEnvironment
        {
            get
            {
                return ms_lastEnvironment;
            }
            private set
            {
                if (ms_lastEnvironment == null && value != null)
                {
                    refCount++;
                }
                else if (ms_lastEnvironment != null && value == null)
                {
                    refCount--;
                }

                ms_lastEnvironment = value;
            }
        }

        public static ScriptEnvironment InvokingEnvironment
        {
            get
            {
                if (CurrentEnvironment.Resource != null && CurrentEnvironment.Resource.State == ResourceState.Parsing)
                {
                    return CurrentEnvironment;
                }

                return (LastEnvironment ?? CurrentEnvironment);
            }
        }

        public Resource Resource
        {
            get
            {
                return m_resource;
            }
        }

        public Lua LuaState
        {
            get
            {
                return ms_luaState;
            }
        }

        public LuaGlobal LuaEnvironment
        {
            get
            {
                return m_luaEnvironment;
            }
        }

        /*public LuaState NativeLuaState
        {
            get
            {
                return m_luaNative;
            }
        }*/

        private static Random ms_instanceGen;

        public uint InstanceID { get; set; }

        static ScriptEnvironment()
        {
            var types = Assembly.GetExecutingAssembly().GetTypes();

            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                foreach (var method in methods)
                {
                    var luaAttribute = method.GetCustomAttribute<LuaMemberAttribute>();

                    if (luaAttribute != null)
                    {
                        ms_luaFunctions.Add(new KeyValuePair<string, MethodInfo>(luaAttribute.Name, method));
                    }
                }
            }

            ms_instanceGen = new Random();

            Extensions.Initialize();
        }

        public ScriptEnvironment(Resource resource)
        {
            m_resource = resource;

            InstanceID = (uint)ms_instanceGen.Next();
        }

        private static LuaChunk[] ms_initChunks;

        private List<LuaChunk> m_curChunks = new List<LuaChunk>();

        public bool Create()
        {
            ScriptEnvironment lastEnvironment = null, oldLastEnvironment = null;

            try
            {
                if (ms_luaState == null)
                {
                    ms_luaState = new Lua();

                    //ms_luaDebug = new LuaStackTraceDebugger();
                    ms_luaDebug = null;

                    ms_initChunks = new []
                    {
                        ms_luaState.CompileChunk("system/MessagePack.lua", ms_luaDebug),
                        ms_luaState.CompileChunk("system/dkjson.lua", ms_luaDebug),
                        ms_luaState.CompileChunk("system/resource_init.lua", ms_luaDebug)
                    };
                }

                m_luaEnvironment = ms_luaState.CreateEnvironment();

                foreach (var func in ms_luaFunctions)
                {
                    m_luaEnvironment[func.Key] = Delegate.CreateDelegate
                    (
                        Expression.GetDelegateType
                        (
                            func.Value.GetParameters()
                                .Select(p => p.ParameterType)
                                .Concat(new Type[] { func.Value.ReturnType })
                                .ToArray()
                        ),
                        null,
                        func.Value
                    );
                }

                InitHandler = null;

                /*m_luaNative = LuaL.LuaLNewState();
                LuaL.LuaLOpenLibs(m_luaNative);

                LuaLib.LuaNewTable(m_luaNative);
                LuaLib.LuaSetGlobal(m_luaNative, "luanet");

                InitHandler = null;

                m_luaState = new NLua.Lua(m_luaNative);*/

                lock (m_luaEnvironment)
                {
                    lastEnvironment = ms_currentEnvironment;
                    ms_currentEnvironment = this;

                    oldLastEnvironment = LastEnvironment;
                    LastEnvironment = lastEnvironment;

                    // load global data files
                    foreach (var chunk in ms_initChunks)
                    {
                        m_luaEnvironment.DoChunk(chunk);
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                this.Log().Error(() => "Error creating script environment for resource " + m_resource.Name + ": " + e.Message, e);

                if (e.InnerException != null)
                {
                    this.Log().Error(() => "Inner exception: " + e.InnerException.Message, e.InnerException);
                }
            }
            finally
            {
                ms_currentEnvironment = lastEnvironment;
                LastEnvironment = oldLastEnvironment;
            }

            return false;
        }

        public void Dispose()
        {
            if (ms_currentEnvironment == this)
            {
                throw new InvalidOperationException("Tried to dispose the current script environment");
            }

            var field = ms_luaState.GetType().GetField("setMemberBinder", BindingFlags.NonPublic | BindingFlags.Instance);
            var binders = (Dictionary<string, System.Runtime.CompilerServices.CallSiteBinder>)field.GetValue(ms_luaState);

            Console.WriteLine("--- BOUNDARY ---");

            foreach (var binder in binders)
            {
                var fields = binder.Value.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

                if (fields.Length < 2)
                {
                    continue;
                }

                field = fields[1];
                var cache = (Dictionary<Type, Object>)field.GetValue(binder.Value);

                if (cache == null)
                {
                    continue;
                }

                foreach (var val in cache)
                {
                    field = val.Value.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(a => a.Name == "_rules");

                    if (field != null)
                    {
                        var rules = field.GetValue(val.Value);

                        var prop = rules.GetType().GetProperty("Length");
                        Console.WriteLine("{0}: {1}", binder.Key, prop.GetValue(rules));
                    }
                }
            }

            m_curChunks.Clear();

            GC.Collect();
        }

        public Delegate InitHandler { get; set; }

        public bool LoadScripts()
        {
            ScriptEnvironment lastEnvironment = null, oldLastEnvironment = null;

            try
            {
                lastEnvironment = ms_currentEnvironment;
                ms_currentEnvironment = this;

                oldLastEnvironment = LastEnvironment;
                LastEnvironment = lastEnvironment;

                // load scripts defined in this resource
                foreach (var script in m_resource.ServerScripts)
                {
                    lock (m_luaEnvironment)
                    {
                        var chunk = ms_luaState.CompileChunk(Path.Combine(m_resource.Path, script), ms_luaDebug);
                        m_luaEnvironment.DoChunk(chunk);
                        m_curChunks.Add(chunk);
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                this.Log().Error(() => "Error creating script environment for resource " + m_resource.Name + ": " + e.Message, e);

                if (e.InnerException != null)
                {
                    this.Log().Error(() => "Inner exception: " + e.InnerException.Message, e.InnerException);
                }
            }
            finally
            {
                ms_currentEnvironment = lastEnvironment;
                LastEnvironment = oldLastEnvironment;
            }

            return false;
        }

        private List<string> m_serverScripts = new List<string>();

        public void AddServerScript(string script)
        {
            m_serverScripts.Add(script);
        }

        public bool DoInitFile(bool preParse)
        {
            ScriptEnvironment lastEnvironment = null, oldLastEnvironment = null;

            try
            {
                lastEnvironment = ms_currentEnvironment;
                ms_currentEnvironment = this;

                oldLastEnvironment = LastEnvironment;
                LastEnvironment = lastEnvironment;

                lock (m_luaEnvironment)
                {
                    var initFunction = ms_luaState.CompileChunk(Path.Combine(m_resource.Path, "__resource.lua"), ms_luaDebug);
                    var initDelegate = new Func<LuaResult>(() => m_luaEnvironment.DoChunk(initFunction));

                    InitHandler.DynamicInvoke(initDelegate, preParse);
                }

                if (!preParse)
                {
                    foreach (var script in m_serverScripts)
                    {
                        var chunk = ms_luaState.CompileChunk(Path.Combine(m_resource.Path, script), null);
                        m_luaEnvironment.DoChunk(chunk);
                        m_curChunks.Add(chunk);
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                this.Log().Error(() => "Error creating script environment for resource " + m_resource.Name + ": " + e.Message, e);

                if (e.InnerException != null)
                {
                    this.Log().Error(() => "Inner exception: " + e.InnerException.Message, e.InnerException);
                }
            }
            finally
            {
                ms_currentEnvironment = lastEnvironment;
                LastEnvironment = oldLastEnvironment;
            }

            return false;
        }

        public void TriggerEvent(string eventName, string argsSerialized, int source)
        {
            List<Delegate> eventHandlers;

            if (!m_eventHandlers.TryGetValue(eventName, out eventHandlers))
            {
                return;
            }

            lock (m_luaEnvironment)
            {
                m_luaEnvironment["source"] = source;

                var lastEnvironment = ms_currentEnvironment;
                ms_currentEnvironment = this;

                var oldLastEnvironment = LastEnvironment;
                LastEnvironment = lastEnvironment;

                //var unpacker = (Func<object, LuaResult>)((LuaTable)m_luaEnvironment["msgpack"])["unpack"];
                //var table = unpacker(argsSerialized);

                dynamic luaEnvironment = m_luaEnvironment;
                LuaTable table = luaEnvironment.msgpack.unpack(argsSerialized);

                var args = new object[table.Length];
                var i = 0;

                foreach (var value in table)
                {
                    args[i] = value.Value;
                    i++;
                }

                foreach (var handler in eventHandlers)
                {
                    try
                    {
                        handler.DynamicInvoke(args.Take(handler.Method.GetParameters().Length - 1).ToArray());
                    }
                    catch (Exception e)
                    {
                        this.Log().Error(() => "Error executing event handler for event " + eventName + " in resource " + m_resource.Name + ": " + e.Message, e);

                        if (e.InnerException != null)
                        {
                            this.Log().Error(() => "Inner exception: " + e.InnerException.Message, e.InnerException);
                        }

                        Game.RconPrint.Print("Error in resource {0}: {1}\n", m_resource.Name, e.Message);

                        eventHandlers.Clear();

                        ms_currentEnvironment = lastEnvironment;
                        LastEnvironment = oldLastEnvironment;

                        return;
                    }
                }

                ms_currentEnvironment = lastEnvironment;
                LastEnvironment = oldLastEnvironment;
            }
        }

        private int m_referenceNum;
        private Dictionary<int, Delegate> m_luaReferences = new Dictionary<int, Delegate>();

        public Delegate GetRef(int reference)
        {
            var func = m_luaReferences[reference];

            return func;
        }

        public string CallExport(Delegate func, string argsSerialized)
        {
            lock (m_luaEnvironment)
            {
                var lastEnvironment = ms_currentEnvironment;
                ms_currentEnvironment = this;

                var oldLastEnvironment = LastEnvironment;
                LastEnvironment = lastEnvironment;

                // unpack
                var unpacker = (Func<string, LuaTable>)((LuaTable)m_luaEnvironment["msgpack"])["unpack"];
                var table = unpacker(argsSerialized);

                var args = new object[table.Length];
                var i = 0;

                foreach (var value in table)
                {
                    args[i] = value.Value;
                    i++;
                }

                // invoke
                var objects = (LuaResult)func.DynamicInvoke(args.Take(func.Method.GetParameters().Length - 1).ToArray());

                // pack return values
                var retstr = EventScriptFunctions.SerializeArguments(objects);

                ms_currentEnvironment = lastEnvironment;
                LastEnvironment = oldLastEnvironment;

                return retstr;
            }
        }

        public int AddRef(Delegate method)
        {
            int refNum = m_referenceNum++;

            m_luaReferences.Add(refNum, method);

            return refNum;
        }

        public bool HasRef(int reference)
        {
            return m_luaReferences.ContainsKey(reference);
        }

        public void RemoveRef(int reference)
        {
            m_luaReferences.Remove(reference);
        }

        class ScriptTimer
        {
            public Delegate Function { get; set; }
            public DateTime TickFrom { get; set; }
        }

        private List<ScriptTimer> m_timers = new List<ScriptTimer>();

        public void Tick()
        {
            var timers = m_timers.GetRange(0, m_timers.Count);
            var now = DateTime.UtcNow;

            foreach (var timer in timers)
            {
                if (now >= timer.TickFrom)
                {
                    lock (m_luaEnvironment)
                    {
                        var lastEnvironment = ms_currentEnvironment;
                        ms_currentEnvironment = this;

                        var oldLastEnvironment = LastEnvironment;
                        LastEnvironment = lastEnvironment;

                        timer.Function.DynamicInvoke();

                        ms_currentEnvironment = lastEnvironment;
                        LastEnvironment = oldLastEnvironment;

                        m_timers.Remove(timer);
                    }
                }
            }
        }

        public void SetTimeout(int milliseconds, Delegate callback)
        {
            var newSpan = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);

            m_timers.Add(new ScriptTimer() { TickFrom = newSpan, Function = callback });
        }

        [LuaMember("SetTimeout")]
        static void SetTimeout_f(int milliseconds, Delegate callback)
        {
            ms_currentEnvironment.SetTimeout(milliseconds, callback);
        }
        
        [LuaMember("AddEventHandler")]
        static void AddEventHandler_f(string eventName, Delegate eventHandler)
        {
            ms_currentEnvironment.AddEventHandler(eventName, eventHandler);
        }

        [LuaMember("GetInstanceId")]
        static int GetInstanceId_f()
        {
            return (int)ms_currentEnvironment.InstanceID;
        }

        private Dictionary<string, List<Delegate>> m_eventHandlers = new Dictionary<string, List<Delegate>>();

        public void AddEventHandler(string eventName, Delegate eventHandler)
        {
            if (!m_eventHandlers.ContainsKey(eventName))
            {
                m_eventHandlers[eventName] = new List<Delegate>();
            }

            m_eventHandlers[eventName].Add(eventHandler);
        }
    }
}
/*
namespace CitizenMP.Server
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    sealed class LuaFunctionAttribute : Attribute
    {
        public LuaFunctionAttribute(string functionName)
        {
            FunctionName = functionName;
        }

        public string FunctionName { get; private set; }
    }
}
*/