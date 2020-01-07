using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using KeraLua;

using NLua.Method;
using NLua.Exceptions;
using NLua.Extensions;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;

namespace NLua {
    public class ObjectTranslator {
        // Compare cache entries by exact reference to avoid unwanted aliases
        private class ReferenceComparer : IEqualityComparer<object> {
            public new bool Equals(object x, object y) {
                if (x != null && y != null && x.GetType() == y.GetType() && x.GetType().IsValueType && y.GetType().IsValueType)
                    return x.Equals(y); // Special case for boxed value types
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj) {
                return obj.GetHashCode();
            }
        }

        private static readonly LuaNativeFunction _registerTableFunction = RegisterTable;
        private static readonly LuaNativeFunction _unregisterTableFunction = UnregisterTable;
        private static readonly LuaNativeFunction _getMethodSigFunction = GetMethodSignature;
        private static readonly LuaNativeFunction _getConstructorSigFunction = GetConstructorSignature;
        private static readonly LuaNativeFunction _importTypeFunction = ImportType;
        private static readonly LuaNativeFunction _loadAssemblyFunction = LoadAssembly;
        private static readonly LuaNativeFunction _ctypeFunction = CType;
        private static readonly LuaNativeFunction _enumFromIntFunction = EnumFromInt;

        // object to object #
        readonly Dictionary<object, int> _mapObjectToRef = new Dictionary<object, int>(new ReferenceComparer());
        // object # to object (FIXME - it should be possible to get object address as an object #)
        readonly Dictionary<int, object> _mapRefToObject = new Dictionary<int, object>();

        readonly ConcurrentQueue<int> finalizedReferences = new ConcurrentQueue<int>();

        internal EventHandlerContainer PendingEvents = new EventHandlerContainer();
        readonly List<Assembly> assemblies = new List<Assembly>();

        /// <summary>
        /// We want to ensure that objects always have a unique ID
        /// </summary>
        int _nextObj;

        public readonly MetaFunctions Meta;
        internal readonly CheckType Checker;
        public readonly IntPtr Tag;
        public readonly Lua Lua;
        public LuaState State => Lua.State;

        public ObjectTranslator(Lua lua) {
            Tag = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(int)));
            Lua = lua;
            Checker = new CheckType(this);
            Meta = new MetaFunctions(this);

            CreateLuaObjectList();
            CreateIndexingMetaFunction();
            CreateBaseClassMetatable();
            CreateClassMetatable();
            CreateFunctionMetatable();
            SetGlobalFunctions();
        }

        private void SetMetaFunction(string lua, string sharp) {
            State.PushString("__" + lua);
            State.PushCFunction(Meta[sharp]);
            State.SetTable(-3);
        }

        private void SetMetaFunction(string lua, string sharp, LuaNativeFunction f) {
            State.PushString("__" + lua);
            State.PushCFunction(Meta[sharp] = f);
            State.SetTable(-3);
        }

        /// <summary>
        /// Sets up the list of objects in the Lua side
        /// </summary>
        private void CreateLuaObjectList() {
            State.PushString("luaNet_objects");
            State.NewTable();
            State.NewTable();
            State.PushString("__mode");
            State.PushString("v");
            State.SetTable(-3);
            State.SetMetaTable(-2);
            State.SetTable((int) LuaRegistry.Index);
        }

        /// <summary>
        /// Registers the indexing function of CLR objects
        /// passed to Lua
        /// </summary>
        private void CreateIndexingMetaFunction() {
            State.PushString("luaNet_indexfunction");
            State.DoEmbeddedFile("metaindex");
            State.RawSet(LuaRegistry.Index);
        }

        /// <summary>
        /// Creates the metatable for superclasses (the base
        /// field of registered tables)
        /// </summary>
        private void CreateBaseClassMetatable() {
            State.NewMetaTable("luaNet_searchbase");
            SetMetaFunction("gc", "Gc");
            SetMetaFunction("tostring", "ToStringLua");
            SetMetaFunction("index", "BaseIndex");
            SetMetaFunction("newindex", "NewIndex");
            State.SetTop(-2);
        }

        /// <summary>
        /// Creates the metatable for type references
        /// </summary>
        private void CreateClassMetatable() {
            State.NewMetaTable("luaNet_class");
            SetMetaFunction("gc", "Gc");
            SetMetaFunction("tostring", "ToStringLua");
            SetMetaFunction("index", "ClassIndex");
            SetMetaFunction("newindex", "ClassNewIndex");
            SetMetaFunction("call", "CallConstructor");
            State.SetTop(-2);
        }

        /// <summary>
        /// Registers the global functions used by NLua
        /// </summary>
        private void SetGlobalFunctions() {
            State.PushCFunction(Meta["Index"]);
            State.SetGlobal("get_object_member");
            State.PushCFunction(_importTypeFunction);
            State.SetGlobal("import_type");
            State.PushCFunction(_loadAssemblyFunction);
            State.SetGlobal("load_assembly");
            State.PushCFunction(_registerTableFunction);
            State.SetGlobal("make_object");
            State.PushCFunction(_unregisterTableFunction);
            State.SetGlobal("free_object");
            State.PushCFunction(_getMethodSigFunction);
            State.SetGlobal("get_method_bysig");
            State.PushCFunction(_getConstructorSigFunction);
            State.SetGlobal("get_constructor_bysig");
            State.PushCFunction(_ctypeFunction);
            State.SetGlobal("ctype");
            State.PushCFunction(_enumFromIntFunction);
            State.SetGlobal("enum");
        }

        /// <summary>
        /// Creates the metatable for delegates
        /// </summary>
        private void CreateFunctionMetatable() {
            State.NewMetaTable("luaNet_function");
            SetMetaFunction("gc", "Gc");
            SetMetaFunction("call", "ExecuteDelegate");
            State.SetTop(-2);
        }

        /// <summary>
        /// Passes errors (argument e) to the Lua interpreter
        /// </summary>
        internal int ThrowError(int rvc, object e) {
            // We use this to remove anything pushed by luaL_where
            int oldTop = State.GetTop();

            // Stack frame #1 is our C# wrapper, so not very interesting to the user
            // Stack frame #2 must be the lua code that called us, so that's what we want to use
            State.Where(1);
            object[] curlev = PopValues(oldTop);

            // Determine the position in the script where the exception was triggered
            string errLocation = string.Empty;

            if (curlev.Length > 0)
                errLocation = curlev[0].ToString();


            if (e is string message) {
                // Wrap Lua error (just a string) and store the error location
                if (Lua.UseTraceback)
                    message += Environment.NewLine + Lua.GetDebugTraceback();
                e = new LuaScriptException(message, errLocation);
            } else if (e is Exception ex) {
                // Wrap generic .NET exception as an InnerException and store the error location
                if (Lua.UseTraceback)
                    ex.Data["Traceback"] = Lua.GetDebugTraceback();
                e = new LuaScriptException(ex, errLocation);
            }

            Push(e);
            State.Error();

            for (int i = 0; i < rvc; i++)
                State.PushNil();
            return rvc;
        }

        /// <summary>
        /// Implementation of load_assembly. Throws an error
        /// if the assembly is not found.
        /// </summary>
        private static int LoadAssembly(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.LoadAssemblyInternal();
        }

        private int LoadAssemblyInternal() {
            try {
                string assemblyName = State.ToString(1, false);
                Assembly assembly = null;
                Exception exception = null;

                try {
                    assembly = Assembly.Load(assemblyName);
                } catch (BadImageFormatException) {
                    // The assemblyName was invalid.  It is most likely a path.
                } catch (FileNotFoundException e) {
                    exception = e;
                }

                if (assembly == null) {
                    try {
                        assembly = Assembly.Load(AssemblyName.GetAssemblyName(assemblyName));
                    } catch (FileNotFoundException e) {
                        exception = e;
                    }
                    if (assembly == null) {
                        AssemblyName mscor = assemblies[0].GetName();
                        AssemblyName name = new AssemblyName();
                        name.Name = assemblyName;
                        name.CultureInfo = mscor.CultureInfo;
                        name.Version = mscor.Version;
                        name.SetPublicKeyToken(mscor.GetPublicKeyToken());
                        name.SetPublicKey(mscor.GetPublicKey());
                        assembly = Assembly.Load(name);

                        if (assembly != null)
                            exception = null;
                    }
                    if (exception != null)
                        return ThrowError(0, exception);
                }
                if (assembly != null && !assemblies.Contains(assembly))
                    assemblies.Add(assembly);
            } catch (Exception e) {
                return ThrowError(0, e);
            }
            return 0;
        }

        internal Type FindType(string className) {
            foreach (Assembly assembly in assemblies) {
                Type type = assembly.GetType(className);

                if (type != null)
                    return type;
            }
            return null;
        }

        public bool TryGetExtensionMethod(Type type, string name, out MethodInfo method) {
            method = GetExtensionMethod(type, name);
            return method != null;
        }

        public MethodInfo GetExtensionMethod(Type type, string name) {
            return type.GetExtensionMethod(name, assemblies);
        }

        /// <summary>
        /// Implementation of import_type. Returns nil if the
        /// type is not found.
        /// </summary>
        private static int ImportType(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.ImportTypeInternal();
        }

        private int ImportTypeInternal() {
            string className = State.ToString(1, false);
            Type type = FindType(className);

            if (type != null)
                PushType(type);
            else
                State.PushNil();

            return 1;
        }

        /// <summary>
        /// Implementation of make_object. Registers a table (first
        /// argument in the stack) as an object subclassing the
        /// type passed as second argument in the stack.
        /// </summary>
        private static int RegisterTable(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.RegisterTableInternal();
        }

        private int RegisterTableInternal() {
            if (State.Type(1) != LuaType.Table)
                return ThrowError(0, "register_table: first arg is not a table");

            LuaTable luaTable = GetTable(1);
            string superclassName = State.ToString(2, false);

            if (string.IsNullOrEmpty(superclassName))
                return ThrowError(0, "register_table: superclass name can not be null");

            Type type = FindType(superclassName);

            if (type == null)
                return ThrowError(0, "register_table: can not find superclass '" + superclassName + "'");

            // Creates and pushes the object in the stack, setting
            // it as the  metatable of the first argument
            object obj = CodeGeneration.GetClassInstance(type, luaTable);
            PushObject(obj, "luaNet_metatable");
            State.NewTable();
            State.PushString("__index");
            State.PushCopy(-3);
            State.SetTable(-3);
            State.PushString("__newindex");
            State.PushCopy(-3);
            State.SetTable(-3);
            State.SetMetaTable(1);

            // Pushes the object again, this time as the base field
            // of the table and with the luaNet_searchbase metatable
            State.PushString("base");
            int index = AddObject(obj);
            PushNewObject(obj, index, "luaNet_searchbase");
            State.RawSet(1);

            return 0;
        }

        /// <summary>
        /// Implementation of free_object. Clears the metatable and the
        /// base field, freeing the created object for garbage-collection
        /// </summary>
        private static int UnregisterTable(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.UnregisterTableInternal();
        }

        private int UnregisterTableInternal() {
            if (!State.GetMetaTable(1))
                return ThrowError(0, "unregister_table: arg is not valid table");

            State.PushString("__index");
            State.GetTable(-2);
            object obj = GetRawNetObject(-1);

            if (obj == null)
                return ThrowError(0, "unregister_table: arg is not valid table");

            FieldInfo luaTableField = obj.GetType().GetField("__luaInterface_luaTable");

            if (luaTableField == null)
                return ThrowError(0, "unregister_table: arg is not valid table");

            // ReSharper disable once PossibleNullReferenceException
            luaTableField.SetValue(obj, null);
            State.PushNil();
            State.SetMetaTable(1);
            State.PushString("base");
            State.PushNil();
            State.SetTable(1);

            return 0;
        }

        /// <summary>
        /// Implementation of get_method_bysig. Returns nil
        /// if no matching method is not found.
        /// </summary>
        private static int GetMethodSignature(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.GetMethodSignatureInternal();
        }

        private int GetMethodSignatureInternal() {
            Type type;
            object target;
            int udata = State.AsUserdataType(1, "luaNet_class");

            if (udata != -1) {
                type = ((LuaStaticMemberProxy) _mapRefToObject[udata]).Type;
                target = null;
            } else {
                target = GetRawNetObject(1);

                if (target == null)
                    return ThrowError(1, "get_method_bysig: first arg is not type or object reference");

                type = target.GetType();
            }

            string methodName = State.ToString(2, false);
            Type[] signature = new Type[State.GetTop() - 2];

            for (int i = 0; i < signature.Length; i++)
                signature[i] = FindType(State.ToString(i + 3, false));

            try {
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, signature, null);
                LuaMethodWrapper wrapper = new LuaMethodWrapper(this, type, method);
                LuaNativeFunction invokeDelegate = wrapper.InvokeFunction;
                PushFunction(invokeDelegate);
            } catch (Exception e) {
                return ThrowError(1, e);
            }

            return 1;
        }

        /// <summary>
        /// Implementation of get_constructor_bysig. Returns nil
        /// if no matching constructor is found.
        /// </summary>
        private static int GetConstructorSignature(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.GetConstructorSignatureInternal();
        }

        private int GetConstructorSignatureInternal() {
            Type type = null;
            int udata = State.AsUserdataType(1, "luaNet_class");

            if (udata != -1)
                type = ((LuaStaticMemberProxy) _mapRefToObject[udata]).Type;

            if (type == null)
                return ThrowError(1, "get_constructor_bysig: first arg is invalid type reference");

            Type[] signature = new Type[State.GetTop() - 1];

            for (int i = 0; i < signature.Length; i++)
                signature[i] = FindType(State.ToString(i + 2, false));

            try {
                ConstructorInfo constructor = type.GetConstructor(signature);
                LuaMethodWrapper wrapper = new LuaMethodWrapper(this, type, constructor);
                LuaNativeFunction invokeDelegate = wrapper.InvokeFunction;
                PushFunction(invokeDelegate);
            } catch (Exception e) {
                return ThrowError(1, e);
            }
            return 1;
        }

        /// <summary>
        /// Pushes a type reference into the stack
        /// </summary>
        internal void PushType(Type t) {
            PushObject(new LuaStaticMemberProxy(t), "luaNet_class");
        }

        /// <summary>
        /// Pushes a delegate into the stack
        /// </summary>
        internal void PushFunction(LuaNativeFunction func) {
            PushObject(func, "luaNet_function");
        }


        /// <summary>
        /// Pushes a CLR object into the Lua stack as an userdata
        /// with the provided metatable
        /// </summary>
        internal void PushObject(object o, string metatable) {
            int index = -1;

            // Pushes nil
            if (o == null) {
                State.PushNil();
                return;
            }

            // Object already in the list of Lua objects? Push the stored reference.
            bool found = (!o.GetType().IsValueType || o.GetType().IsEnum) && _mapObjectToRef.TryGetValue(o, out index);

            if (found) {
                State.GetMetaTable("luaNet_objects");
                State.RawGetInteger(-1, index);

                // Note: starting with lua5.1 the garbage collector may remove weak reference items (such as our luaNet_objects values) when the initial GC sweep 
                // occurs, but the actual call of the __gc finalizer for that object may not happen until a little while later.  During that window we might call
                // this routine and find the element missing from luaNet_objects, but collectObject() has not yet been called.  In that case, we go ahead and call collect
                // object here
                // did we find a non nil object in our table? if not, we need to call collect object
                LuaType type = State.Type(-1);
                if (type != LuaType.Nil) {
                    State.Remove(-2);	 // drop the metatable - we're going to leave our object on the stack
                    return;
                }

                State.Remove(-1);	// remove the nil object value
                State.Remove(-1);	// remove the metatable
                CollectObject(o, index);	// Remove from both our tables and fall out to get a new ID
            }

            index = AddObject(o);
            PushNewObject(o, index, metatable);
        }

        /// <summary>
        /// Pushes a new object into the Lua stack with the provided
        /// metatable
        /// </summary>
        private void PushNewObject(object o, int index, string metatable) {
            if (metatable == "luaNet_metatable") {
                // Gets or creates the metatable for the object's type
                State.GetMetaTable(o.GetType().AssemblyQualifiedName);

                if (State.IsNil(-1)) {
                    State.SetTop(-2);
                    State.NewMetaTable(o.GetType().AssemblyQualifiedName);
                    State.PushString("cache");
                    State.NewTable();
                    State.RawSet(-3);
                    State.PushLightUserData(Tag);
                    State.PushNumber(1);
                    State.RawSet(-3);
                    State.PushString("__index");
                    State.PushString("luaNet_indexfunction");
                    State.RawGet(LuaRegistry.Index);
                    State.RawSet(-3);
                    SetMetaFunction("gc", "Gc");
                    SetMetaFunction("tostring", "ToStringLua");
                    SetMetaFunction("newindex", "NewIndex");
                    // Bind C# operator with Lua metamethods (__add, __sub, __mul)
                    RegisterOperatorsFunctions(o.GetType());
                    RegisterCallMethodForDelegate(o);
                }
            } else
                State.GetMetaTable(metatable);

            // Stores the object index in the Lua list and pushes the
            // index into the Lua stack
            State.GetMetaTable("luaNet_objects");
            State.NewUserDataWithValue(index);
            State.PushCopy(-3);
            State.Remove(-4);
            State.SetMetaTable(-2);
            State.PushCopy(-1);
            State.RawSetInteger(-3, index);
            State.Remove(-2);
        }

        void RegisterCallMethodForDelegate(object o) {
            if (!(o is Delegate))
                return;

            SetMetaFunction("call", "CallDelegate");
        }

        void RegisterOperatorsFunctions(Type type) {
            RegisterOperatorFunction(type, "add", "Addition");
            RegisterOperatorFunction(type, "sub", "Subtraction");
            RegisterOperatorFunction(type, "mul", "Multiply");
            RegisterOperatorFunction(type, "div", "Division");
            RegisterOperatorFunction(type, "mod", "Modulus");
            RegisterOperatorFunction(type, "unm", "UnaryNegation");
            RegisterOperatorFunction(type, "eq", "Equality");
            RegisterOperatorFunction(type, "lt", "LessThan");
            RegisterOperatorFunction(type, "le", "LessThanOrEqual");
        }

        void RegisterOperatorFunction(Type type, string lua, string sharp) {
            string opsharp = "op_" + sharp;
            if (type.IsPrimitive || type.HasMethod(opsharp)) {
                SetMetaFunction(lua, sharp, _ => Meta.MatchOperator(opsharp));
            }
        }

        /// <summary>
        /// Gets an object from the Lua stack with the desired type, if it matches, otherwise
        /// returns null.
        /// </summary>
        internal object GetAsType(int stackPos, Type paramType) {
            ExtractValue extractor = Checker.CheckLuaType(State, stackPos, paramType);
            return extractor != null ? extractor(State, stackPos) : null;
        }

        /// <summary>
        /// Given the Lua int ID for an object remove it from our maps
        /// </summary>
        /// <param name = "udata"></param>
        internal void CollectObject(int udata) {
            // The other variant of collectObject might have gotten here first, in that case we will silently ignore the missing entry
            if (_mapRefToObject.TryGetValue(udata, out object o))
                CollectObject(o, udata);
        }

        /// <summary>
        /// Given an object reference, remove it from our maps
        /// </summary>
        /// <param name = "o"></param>
        /// <param name = "udata"></param>
        private void CollectObject(object o, int udata) {
            _mapRefToObject.Remove(udata);
            if (!o.GetType().IsValueType || o.GetType().IsEnum)
                _mapObjectToRef.Remove(o);
        }

        private int AddObject(object obj) {
            // New object: inserts it in the list
            int index = _nextObj++;
            _mapRefToObject[index] = obj;

            if (!obj.GetType().IsValueType || obj.GetType().IsEnum)
                _mapObjectToRef[obj] = index;

            return index;
        }

        /// <summary>
        /// Gets an object from the Lua stack according to its Lua type.
        /// </summary>
        internal object GetObject(int index) {
            LuaType type = State.Type(index);

            switch (type) {
                case LuaType.Number:
                    if (State.IsInteger(index))
                        return State.ToInteger(index);

                    return State.ToNumber(index);
                case LuaType.String:
                    return State.ToString(index, false);
                case LuaType.Boolean:
                    return State.ToBoolean(index);
                case LuaType.Table:
                    return GetTable(index);
                case LuaType.Function:
                    return GetFunction(index);
                case LuaType.Thread:
                    return GetThread(index);
                case LuaType.UserData:
                    int udata = State.ToNetObject(index, Tag);
                    return udata != -1 ? _mapRefToObject[udata] : GetUserData(index);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets the table in the index positon of the Lua stack.
        /// </summary>
        internal LuaTable GetTable(int index) {
            // Before create new tables, check if there is any finalized object to clean.
            CleanFinalizedReferences();

            State.PushCopy(index);
            int reference = State.Ref(LuaRegistry.Index);
            if (reference == -1)
                return null;
            return new LuaTable(reference, Lua);
        }

        /// <summary>
        /// Gets the userdata in the index positon of the Lua stack.
        /// </summary>
        internal LuaUserData GetUserData(int index) {
            // Before create new tables, check if there is any finalized object to clean.
            CleanFinalizedReferences();

            State.PushCopy(index);
            int reference = State.Ref(LuaRegistry.Index);
            if (reference == -1)
                return null;
            return new LuaUserData(reference, Lua);
        }

        /// <summary>
        /// Gets the function in the index positon of the Lua stack.
        /// </summary>
        internal LuaFunction GetFunction(int index) {
            // Before create new tables, check if there is any finalized object to clean.
            CleanFinalizedReferences();

            State.PushCopy(index);
            int reference = State.Ref(LuaRegistry.Index);
            if (reference == -1)
                return null;
            return new LuaFunction(reference, Lua);
        }

        /// <summary>
        /// Gets the thread in the index position of the Lua stack.
        /// </summary>
        internal LuaThread GetThread(int index) {
            // Before create new tables, check if there is any finalized object to clean.
            CleanFinalizedReferences();

            State.PushCopy(index);
            int reference = State.Ref(LuaRegistry.Index);
            if (reference == -1)
                return null;
            return new LuaThread(reference, Lua);
        }

        /// <summary>
        /// Gets the CLR object in the index positon of the Lua stack. Returns
        /// delegates as Lua functions.
        /// </summary>
        internal object GetNetObject(int index) {
            int idx = State.ToNetObject(index, Tag);
            return idx != -1 ? _mapRefToObject[idx] : null;
        }

        /// <summary>
        /// Gets the CLR object in the index position of the Lua stack. Returns
        /// delegates as is.
        /// </summary>
        internal object GetRawNetObject(int index) {
            int udata = State.ToRawNetObject(index);
            return udata != -1 ? _mapRefToObject[udata] : null;
        }


        /// <summary>
        /// Gets the values from the provided index to
        /// the top of the stack and returns them in an array.
        /// </summary>
        internal object[] PopValues(int oldTop) {
            int newTop = State.GetTop();

            if (oldTop == newTop)
                return null;

            List<object> returnValues = new List<object>();
            for (int i = oldTop + 1; i <= newTop; i++)
                returnValues.Add(GetObject(i));

            State.SetTop(oldTop);
            return returnValues.ToArray();
        }

        /// <summary>
        /// Gets the values from the provided index to
        /// the top of the stack and returns them in an array, casting
        /// them to the provided types.
        /// </summary>
        internal object[] PopValues(int oldTop, Type[] popTypes) {
            int newTop = State.GetTop();

            if (oldTop == newTop)
                return null;

            int iTypes;
            List<object> returnValues = new List<object>();

            if (popTypes[0] == typeof(void))
                iTypes = 1;
            else
                iTypes = 0;

            for (int i = oldTop + 1; i <= newTop; i++) {
                returnValues.Add(GetAsType(i, popTypes[iTypes]));
                iTypes++;
            }

            State.SetTop(oldTop);
            return returnValues.ToArray();
        }

        // The following line doesn't work for remoting proxies - they always return a match for 'is'
        // else if (o is ILuaGeneratedType)
        private static bool IsILua(object o) {
            if (o is ILuaGeneratedType) {
                // Make sure we are _really_ ILuaGenerated
                Type typ = o.GetType();
                return typ.GetInterface("ILuaGeneratedType", true) != null;
            }
            return false;
        }

        /// <summary>
        /// Pushes the object into the Lua stack according to its type.
        /// </summary>
        internal void Push(object o) {
            switch (o) {
                case null:
                    State.PushNil();
                    break;
                case sbyte sb:
                    State.PushInteger(sb);
                    break;
                case byte bt:
                    State.PushInteger(bt);
                    break;
                case short s:
                    State.PushInteger(s);
                    break;
                case ushort us:
                    State.PushInteger(us);
                    break;
                case int i:
                    State.PushInteger(i);
                    break;
                case uint ui:
                    State.PushInteger(ui);
                    break;
                case long l:
                    State.PushInteger(l);
                    break;
                case ulong ul:
                    State.PushInteger((long) ul);
                    break;
                case char ch:
                    State.PushInteger(ch);
                    break;
                case float fl:
                    State.PushNumber(fl);
                    break;
                case decimal dc:
                    State.PushNumber((double) dc);
                    break;
                case double db:
                    State.PushNumber(db);
                    break;
                case string str:
                    State.PushString(str);
                    break;
                case bool b:
                    State.PushBoolean(b);
                    break;
                case LuaTable table:
                    table._Push();
                    break;
                case LuaNativeFunction nativeFunction:
                    PushFunction(nativeFunction);
                    break;
                case LuaFunction luaFunction:
                    luaFunction._Push();
                    break;
                case LuaThread luaThread:
                    luaThread._Push();
                    break;
                default:
                    if (IsILua(o))
                        ((ILuaGeneratedType) o).LuaInterfaceGetLuaTable()._Push();
                    else
                        PushObject(o, "luaNet_metatable");
                    break;
            }
        }

        private Type TypeOf(int idx) {
            int udata = State.AsUserdataType(idx, "luaNet_class");
            if (udata == -1)
                return null;

            LuaStaticMemberProxy pt = (LuaStaticMemberProxy) _mapRefToObject[udata];
            return pt.Type;
        }

        int PushError(string msg) {
            State.PushNil();
            State.PushString(msg);
            return 2;
        }

        private static int CType(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.CTypeInternal();
        }

        int CTypeInternal() {
            Type t = TypeOf(1);
            if (t == null)
                return PushError("Not a CLR Class");

            PushObject(t, "luaNet_metatable");
            return 1;
        }

        private static int EnumFromInt(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            ObjectTranslator translator = LuaPool.Find(state).Translator;
            return translator.EnumFromIntInternal();
        }

        int EnumFromIntInternal() {
            Type t = TypeOf(1);
            if (t == null || !t.IsEnum)
                return PushError("Not an Enum.");

            object res = null;
            LuaType lt = State.Type(2);
            if (lt == LuaType.Number) {
                int ival = (int) State.ToNumber(2);
                res = Enum.ToObject(t, ival);
            } else if (lt == LuaType.String) {
                string sflags = State.ToString(2, false);
                string err = null;
                try {
                    res = Enum.Parse(t, sflags, true);
                } catch (ArgumentException e) {
                    err = e.Message;
                }
                if (err != null)
                    return PushError(err);
            } else {
                return PushError("Second argument must be a integer or a string.");
            }
            PushObject(res, "luaNet_metatable");
            return 1;
        }

        internal void AddFinalizedReference(int reference) {
            finalizedReferences.Enqueue(reference);
        }

        void CleanFinalizedReferences() {
            while (finalizedReferences.TryDequeue(out int reference))
                State.Unref(LuaRegistry.Index, reference);
        }
    }
}
