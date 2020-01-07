using System;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using KeraLua;

using NLua.Method;
using NLua.Extensions;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;

namespace NLua {
    public class MetaFunctions {
        private readonly Dictionary<Type, Dictionary<string, object>> _MemberCache = new Dictionary<Type, Dictionary<string, object>>();
        private readonly Dictionary<string, LuaNativeFunction> _Functions = new Dictionary<string, LuaNativeFunction>();

        public readonly ObjectTranslator Translator;
        public LuaState State => Translator.State;

        public MetaFunctions(ObjectTranslator translator) {
            Translator = translator;
        }

        public LuaNativeFunction this[string name] {
            get {
                if (_Functions.TryGetValue(name, out LuaNativeFunction f) && f != null)
                    return f;
                MethodInfo method = typeof(MetaFunctions).GetMethod(name);
                if (method == null)
                    throw new ArgumentException($"MetaFunction not found: {name}");
                Func<int> inner = (Func<int>) method.CreateDelegate(typeof(Func<int>), this);
                f = (State) => inner();
                _Functions[name] = f;
                return f;
            }
            set {
                _Functions[name] = value;
            }
        }

        /// <summary>
        /// __call metafunction of CLR delegates, retrieves and calls the delegate.
        /// </summary>
        public int ExecuteDelegate() {
            LuaNativeFunction func = (LuaNativeFunction) Translator.GetRawNetObject(1);
            State.Remove(1);
            return func(State.Handle);
        }

        /// <summary>
        /// __gc metafunction of CLR objects.
        /// </summary>
        public int Gc() {
            int udata = State.ToRawNetObject(1);

            if (udata != -1)
                Translator.CollectObject(udata);

            return 0;
        }

        /// <summary>
        /// __tostring metafunction of CLR objects.
        /// </summary>
        public int ToStringLua() {
            Translator.Push(Translator.GetRawNetObject(1)?.ToString());
            return 1;
        }

        /// <summary>
        /// Called by the __index metafunction of CLR objects in case the
        /// method is not cached or it is a field / property / event.
        /// Receives the object and the member name as arguments and returns
        /// either the value of the member or a delegate to call it.
        /// If the member does not exist returns nil.
        /// </summary>
        public int Index() {
            object obj = Translator.GetRawNetObject(1);

            if (obj == null)
                return Translator.ThrowError(1, "Trying to index an invalid object reference");

            object index = Translator.GetObject(2);
            string methodName = index as string; // will be null if not a string arg
            Type objType = obj.GetType();

            // Handle the most common case, looking up the method by name. 
            // CP: This will fail when using indexers and attempting to get a value with the same name as a property of the object, 
            // ie: xmlelement['item'] <- item is a property of xmlelement

            if (!string.IsNullOrEmpty(methodName) && (
                CheckMemberCache(objType, methodName) != null ||
                objType.GetMember(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0
            ))
                return GetMemberValue(objType, obj, methodName);

            // Try to access by array if the type is right and index is an int (lua numbers always come across as double)
            if (TryAccessByArray(objType, obj, index))
                return 1;

            int fallback = GetMethodFallback(objType, obj, index, methodName);
            if (fallback != 0)
                return fallback;

            if (!string.IsNullOrEmpty(methodName) || index != null) {
                Translator.Push(null);
                Translator.Push(true);
                return 2;
            }

            State.PushBoolean(false);
            return 2;
        }

        private bool TryAccessByArray(Type objType, object obj, object index) {
            if (!objType.IsArray)
                return false;

            int intIndex = -1;
            if (index is long l)
                intIndex = (int) l;
            else if (index is double d)
                intIndex = (int) d;

            if (intIndex == -1)
                return false;

            Array array = (Array) obj;
            object element = array.GetValue(intIndex);
            Translator.Push(element);
            return true;
        }

        public int GetMethodFallback(Type objType, object obj, object index, string methodName) {
            if (!string.IsNullOrEmpty(methodName) && TryGetExtensionMethod(objType, methodName, out object method))
                return PushExtensionMethod(objType, obj, methodName, method);

            // Try to use get_Item to index into this .NET object
            MethodInfo[] methods = objType.GetMethods();

            int res = TryIndexMethods(methods, obj, index);
            if (res != 0)
                return res;

            // Fallback to GetRuntimeMethods
            methods = objType.GetRuntimeMethods().ToArray();

            res = TryIndexMethods(methods, obj, index);
            if (res != 0)
                return res;

            res = TryGetValueForKeyMethods(methods, obj, index);
            if (res != 0)
                return res;

            // Try find explicity interface implementation
            MethodInfo explicitInterfaceMethod =
                objType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == methodName && m.IsPrivate && m.IsVirtual && m.IsFinal);

            if (explicitInterfaceMethod != null) {
                LuaMethodWrapper methodWrapper = new LuaMethodWrapper(Translator, objType, explicitInterfaceMethod);
                LuaNativeFunction invokeDelegate = new LuaNativeFunction(methodWrapper.InvokeFunction);

                SetMemberCache(objType, methodName, invokeDelegate);

                Translator.PushFunction(invokeDelegate);
                Translator.Push(true);
                return 2;
            }

            return 0;
        }

        private bool TryGetExtensionMethod(Type type, string name, out object method) {
            object cachedMember = CheckMemberCache(type, name);

            if (cachedMember != null) {
                method = cachedMember;
                return true;
            }

            bool found = Translator.TryGetExtensionMethod(type, name, out MethodInfo methodInfo);
            method = methodInfo;
            return found;
        }

        private int PushExtensionMethod(Type type, object obj, string name, object method) {
            if (method is LuaNativeFunction cachedFunction) {
                Translator.PushFunction(cachedFunction);
                Translator.Push(true);
                return 2;
            }

            MethodInfo methodInfo = (MethodInfo) method;
            LuaMethodWrapper methodWrapper = new LuaMethodWrapper(Translator, type, methodInfo);
            LuaNativeFunction invokeDelegate = methodWrapper.InvokeFunction;

            SetMemberCache(type, name, invokeDelegate);

            Translator.PushFunction(invokeDelegate);
            Translator.Push(true);
            return 2;
        }

        public int TryGetValueForKeyMethods(MethodInfo[] methods, object obj, object index) {
            foreach (MethodInfo methodInfo in methods) {
                if (methodInfo.Name != "TryGetValueForKey")
                    continue;

                // Check if the signature matches the input
                if (methodInfo.GetParameters().Length != 2)
                    continue;

                ParameterInfo[] actualParams = methodInfo.GetParameters();

                // Get the index in a form acceptable to the getter
                index = Translator.GetAsType(2, actualParams[0].ParameterType);

                // If the index type and the parameter doesn't match, just skip it
                if (index == null)
                    break;

                object[] args = new object[2];

                // Just call the indexer - if out of bounds an exception will happen
                args[0] = index;

                try {
                    bool found = (bool) methodInfo.Invoke(obj, args);

                    if (!found)
                        return Translator.ThrowError(1, "key not found: " + index);

                    Translator.Push(args[1]);
                    return 1;
                } catch (TargetInvocationException e) {
                    // Provide a more readable description for the common case of key not found
                    if (e.InnerException is KeyNotFoundException)
                        return Translator.ThrowError(1, "key '" + index + "' not found ");
                    else
                        return Translator.ThrowError(1, "exception indexing '" + index + "' " + e.Message);
                }
            }
            return 0;
        }

        public int TryIndexMethods(MethodInfo[] methods, object obj, object index) {
            foreach (MethodInfo methodInfo in methods) {
                if (methodInfo.Name != "get_Item")
                    continue;

                // Check if the signature matches the input
                if (methodInfo.GetParameters().Length != 1)
                    continue;

                ParameterInfo[] actualParams = methodInfo.GetParameters();

                // Get the index in a form acceptable to the getter
                index = Translator.GetAsType(2, actualParams[0].ParameterType);

                // If the index type and the parameter doesn't match, just skip it
                if (index == null)
                    break;

                object[] args = new object[1];

                // Just call the indexer - if out of bounds an exception will happen
                args[0] = index;

                try {
                    object result = methodInfo.Invoke(obj, args);
                    Translator.Push(result);
                    return 1;
                } catch (TargetInvocationException e) {
                    // Provide a more readable description for the common case of key not found
                    if (e.InnerException is KeyNotFoundException)
                        return Translator.ThrowError(1, "key '" + index + "' not found ");
                    else
                        return Translator.ThrowError(1, "exception indexing '" + index + "' " + e.Message);
                }
            }
            return 0;
        }

        /// <summary>
        /// __index metafunction of base classes (the base field of Lua tables).
        /// Adds a prefix to the method name to call the base version of the method.
        /// </summary>
        public int BaseIndex() {
            object obj = Translator.GetRawNetObject(1);

            if (obj == null)
                return Translator.ThrowError(2, "Trying to index an invalid object reference");

            string methodName = State.ToString(2, false);

            if (string.IsNullOrEmpty(methodName)) {
                State.PushNil();
                State.PushBoolean(false);
                return 2;
            }

            GetMemberValue(obj.GetType(), obj, "__luaInterface_base_" + methodName);
            State.SetTop(-2);

            if (State.Type(-1) == LuaType.Nil) {
                State.SetTop(-2);
                return GetMemberValue(obj.GetType(), obj, methodName);
            }

            State.PushBoolean(false);
            return 2;
        }

        /// <summary>
        /// Pushes the value of a member or a delegate to call it, depending on the type of
        /// the member. Works with static or instance members.
        /// Uses reflection to find members, and stores the reflected MemberInfo object in
        /// a cache (indexed by the type of the object and the name of the member).
        /// </summary>
        int GetMemberValue(Type objType, object obj, string methodName) {
            MemberInfo member = null;
            object cachedMember = CheckMemberCache(objType, methodName);

            if (cachedMember is LuaNativeFunction) {
                Translator.PushFunction((LuaNativeFunction) cachedMember);
                Translator.Push(true);
                return 2;
            }

            if (cachedMember != null) {
                member = (MemberInfo) cachedMember;
            } else {
                MemberInfo[] members = objType.GetMember(methodName, (obj == null ? BindingFlags.NonPublic : 0) | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

                if (members.Length > 0)
                    member = members[0];
            }

            if (member != null) {
                if (member.MemberType == MemberTypes.Field) {
                    FieldInfo field = (FieldInfo) member;

                    if (cachedMember == null)
                        SetMemberCache(objType, methodName, member);

                    object value = field.GetValue(obj);
                    Translator.Push(value);

                } else if (member.MemberType == MemberTypes.Property) {
                    PropertyInfo property = (PropertyInfo) member;
                    if (cachedMember == null)
                        SetMemberCache(objType, methodName, member);

                    try {
                        object value = property.GetValue(obj, null);
                        Translator.Push(value);
                    } catch (ArgumentException) {
                        // If we can't find the getter in our class, recurse up to the base class and see
                        // if they can help.
                        if (objType != typeof(object))
                            return GetMemberValue(objType.BaseType, obj, methodName);
                        State.PushNil();
                    }

                } else if (member.MemberType == MemberTypes.Event) {
                    EventInfo eventInfo = (EventInfo) member;
                    if (cachedMember == null)
                        SetMemberCache(objType, methodName, member);

                    Translator.Push(new RegisterEventHandler(Translator.PendingEvents, obj, eventInfo));

                } else if (member.MemberType == MemberTypes.NestedType && member.DeclaringType != null) {
                    if (cachedMember == null)
                        SetMemberCache(objType, methodName, member);

                    // Find the name of our class
                    string name = member.Name;
                    Type decType = member.DeclaringType;

                    // Build a new long name and try to find the type by name
                    string longName = decType.FullName + "+" + name;
                    Type nestedType = Translator.FindType(longName);
                    Translator.PushType(nestedType);

                } else {
                    // Member type must be 'method'
                    LuaMethodWrapper methodWrapper = new LuaMethodWrapper(Translator, objType, methodName);
                    LuaNativeFunction wrapper = methodWrapper.InvokeFunction;

                    if (cachedMember == null)
                        SetMemberCache(objType, methodName, wrapper);

                    Translator.PushFunction(wrapper);
                    Translator.Push(true);
                    return 2;
                }

            } else {
                if (objType != typeof(object))
                    return GetMemberValue(objType.BaseType, obj, methodName);

                State.PushNil();
                Translator.Push(true);
                return 2;
            }

            // Push false because we aren't returning a cached value (see metaindex.lua)
            Translator.Push(false);
            return 2;
        }

        /// <summary>
        /// Checks if a MemberInfo object is cached, returning it or null.
        /// </summary>
        object CheckMemberCache(Type objType, string memberName) {
            if (!_MemberCache.TryGetValue(objType, out Dictionary<string, object> members))
                return null;
            if (members == null || !members.TryGetValue(memberName, out object memberValue))
                return null;
            return memberValue;
        }

        /// <summary>
        /// Stores a MemberInfo object in the member cache.
        /// </summary>
        void SetMemberCache(Type objType, string memberName, object member) {
            if (!_MemberCache.TryGetValue(objType, out Dictionary<string, object> members)) {
                members = new Dictionary<string, object>();
                _MemberCache[objType] = members;
            }
            members[memberName] = member;
        }

        /// <summary>
        /// __newindex metafunction of CLR objects. Receives the object,
        /// the member name and the value to be stored as arguments. Throws
        /// and error if the assignment is invalid.
        /// </summary>
        public int NewIndex() {
            object target = Translator.GetRawNetObject(1);

            if (target == null)
                return Translator.ThrowError(0, "trying to index and invalid object reference");

            Type type = target.GetType();

            // First try to look up the parameter as a property name
            if (TrySetMember(type, target, BindingFlags.Instance, out string detailMessage))
                return 0;

            // We didn't find a property name, now see if we can use a [] style this accessor to set array contents
            if (type.IsArray && State.IsNumber(2)) {
                int index = (int) State.ToNumber(2);
                Array arr = (Array) target;
                object val = Translator.GetAsType(3, arr.GetType().GetElementType());
                arr.SetValue(val, index);
            } else {
                // Try to see if we have a this[] accessor
                MethodInfo setter = type.GetMethod("set_Item");
                if (setter != null) {
                    ParameterInfo[] args = setter.GetParameters();
                    Type valueType = args[1].ParameterType;

                    // The new value the user specified 
                    object val = Translator.GetAsType(3, valueType);
                    Type indexType = args[0].ParameterType;
                    object index = Translator.GetAsType(2, indexType);

                    object[] methodArgs = new object[2];

                    // Just call the indexer - if out of bounds an exception will happen
                    methodArgs[0] = index;
                    methodArgs[1] = val;
                    setter.Invoke(target, methodArgs);
                } else {
                    // Pass the original message from trySetMember because it is probably best
                    return Translator.ThrowError(0, detailMessage);
                }
            }

            return 0;
        }

        /// <summary>
        /// Tries to set a named property or field
        /// </summary>
        /// <param name="luaState"></param>
        /// <param name="targetType"></param>
        /// <param name="target"></param>
        /// <param name="bindingType"></param>
        /// <returns>false if unable to find the named member, true for success</returns>
        bool TrySetMember(Type targetType, object target, BindingFlags bindingType, out string detailMessage) {
            detailMessage = null;   // No error yet

            // If not already a string just return - we don't want to call tostring - which has the side effect of 
            // changing the lua typecode to string
            // Note: We don't use isstring because the standard lua C isstring considers either strings or numbers to
            // be true for isstring.
            if (State.Type(2) != LuaType.String) {
                detailMessage = "property names must be strings";
                return false;
            }

            // We only look up property names by string
            string fieldName = State.ToString(2, false);
            if (string.IsNullOrEmpty(fieldName) || !(char.IsLetter(fieldName[0]) || fieldName[0] == '_')) {
                detailMessage = "Invalid property name";
                return false;
            }

            // Find our member via reflection or the cache
            MemberInfo member = (MemberInfo) CheckMemberCache(targetType, fieldName);
            if (member == null) {
                MemberInfo[] members = targetType.GetMember(fieldName, bindingType | BindingFlags.Public);

                if (members.Length <= 0) {
                    detailMessage = "field or property '" + fieldName + "' does not exist";
                    return false;
                }

                member = members[0];
                SetMemberCache(targetType, fieldName, member);
            }

            if (member.MemberType == MemberTypes.Field) {
                FieldInfo field = (FieldInfo) member;
                object val = Translator.GetAsType(3, field.FieldType);

                try {
                    field.SetValue(target, val);
                } catch (Exception e) {
                    Translator.ThrowError(0, e);
                }

                return true;
            }
            if (member.MemberType == MemberTypes.Property) {
                PropertyInfo property = (PropertyInfo) member;
                object val = Translator.GetAsType(3, property.PropertyType);

                try {
                    property.SetValue(target, val, null);
                } catch (Exception e) {
                    Translator.ThrowError(0, e);
                }

                return true;
            }

            detailMessage = "'" + fieldName + "' is not a .NET field or property";
            return false;
        }

        /// <summary>
        /// __index metafunction of type references, works on static members.
        /// </summary>
        public int ClassIndex() {
            Type type = (Translator.GetRawNetObject(1) as LuaStaticMemberProxy)?.Type;

            if (type == null)
                return Translator.ThrowError(1, "Trying to index an invalid type reference");

            if (State.IsNumber(2)) {
                int size = (int) State.ToNumber(2);
                Translator.Push(Array.CreateInstance(type, size));
                return 1;
            }

            string methodName = State.ToString(2, false);

            if (string.IsNullOrEmpty(methodName)) {
                State.PushNil();
                return 1;
            }
            return GetMemberValue(type, null, methodName);
        }

        /// <summary>
        /// __newindex function of type references, works on static members.
        /// </summary>
        public int ClassNewIndex() {
            Type target = (Translator.GetRawNetObject(1) as LuaStaticMemberProxy)?.Type;

            if (target == null)
                return Translator.ThrowError(0, "trying to index an invalid type reference");

            if (!TrySetMember(target, null, BindingFlags.Static, out string detail))
                return Translator.ThrowError(0, detail);
            return 0;
        }

        /// <summary>
        /// __call metafunction of Delegates. 
        /// </summary>
        public int CallDelegate() {
            if (!(Translator.GetRawNetObject(1) is Delegate del))
                return Translator.ThrowError(1, "Trying to invoke a not delegate or callable value");

            State.Remove(1);

            LuaMethodCache validDelegate = new LuaMethodCache();
            MethodBase methodDelegate = del.Method;
            bool isOk = MatchParameters(methodDelegate, validDelegate, 0);

            if (isOk) {
                object result;

                if (methodDelegate.IsStatic)
                    result = methodDelegate.Invoke(null, validDelegate.Args);
                else
                    result = methodDelegate.Invoke(del.Target, validDelegate.Args);

                Translator.Push(result);
                return 1;
            }

            return Translator.ThrowError(1, "Cannot invoke delegate (invalid arguments for  " + methodDelegate.Name + ")");
        }

        /// <summary>
        /// __call metafunction of type references. Searches for and calls
        /// a constructor for the type. Returns nil if the constructor is not
        /// found or if the arguments are invalid. Throws an error if the constructor
        /// generates an exception.
        /// </summary>
        public int CallConstructor() {
            if (!(Translator.GetRawNetObject(1) is LuaStaticMemberProxy type))
                return Translator.ThrowError(1, "Trying to call constructor on an invalid type reference");

            LuaMethodCache validConstructor = new LuaMethodCache();

            State.Remove(1);
            ConstructorInfo[] constructors = type.Type.GetConstructors();

            foreach (ConstructorInfo constructor in constructors) {
                bool isConstructor = MatchParameters(constructor, validConstructor, 0);

                if (!isConstructor)
                    continue;

                try {
                    Translator.Push(constructor.Invoke(validConstructor.Args));
                } catch (Exception e) {
                    Translator.ThrowError(0, e);
                    State.PushNil();
                }
                return 1;
            }

            if (type.Type.IsValueType) {
                int numLuaParams = State.GetTop();
                if (numLuaParams == 0) {
                    Translator.Push(Activator.CreateInstance(type.Type));
                    return 1;
                }
            }

            string constructorName = constructors.Length == 0 ? "unknown" : constructors[0].Name;
            return Translator.ThrowError(1, string.Format("{0} does not contain constructor({1}) argument match",
                type.Type, constructorName));
        }

        bool IsInteger(double x) {
            return Math.Ceiling(x) == x;
        }

        object GetTargetObject(string operation) {
            Type t;
            object target = Translator.GetRawNetObject(1);
            if (target != null) {
                t = target.GetType();
                if (t.HasMethod(operation))
                    return target;
            }
            target = Translator.GetRawNetObject(2);
            if (target != null) {
                t = target.GetType();
                if (t.HasMethod(operation))
                    return target;
            }
            return null;
        }

        public int MatchOperator(string operation) {
            LuaMethodCache validOperator = new LuaMethodCache();

            object target = GetTargetObject(operation);

            if (target == null)
                return Translator.ThrowError(1, "Cannot call " + operation + " on a nil object");

            Type type = target.GetType();
            MethodInfo[] operators = type.GetMethods(operation, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (MethodInfo op in operators) {
                bool isOk = MatchParameters(op, validOperator, 0);

                if (!isOk)
                    continue;

                object result;
                if (op.IsStatic)
                    result = op.Invoke(null, validOperator.Args);
                else
                    result = op.Invoke(target, validOperator.Args);
                Translator.Push(result);
                return 1;
            }

            return Translator.ThrowError(1, "Cannot call (" + operation + ") on object type " + type.Name);
        }

        internal Array TableToArray(ExtractValue extractValue, Type paramArrayType, ref int startIndex, int count) {
            Array paramArray;

            if (count == 0)
                return Array.CreateInstance(paramArrayType, 0);

            object luaParamValue = extractValue(Translator.State, startIndex);
            startIndex++;

            if (luaParamValue is LuaTable) {
                LuaTable table = (LuaTable) luaParamValue;
                IDictionaryEnumerator tableEnumerator = table.GetEnumerator();
                tableEnumerator.Reset();
                paramArray = Array.CreateInstance(paramArrayType, table.Values.Count);

                int paramArrayIndex = 0;

                while (tableEnumerator.MoveNext()) {
                    object value = tableEnumerator.Value;

                    if (paramArrayType == typeof(object)) {
                        if (value != null && value is double && IsInteger((double) value))
                            value = Convert.ToInt32((double) value);
                    }

                    paramArray.SetValue(Convert.ChangeType(value, paramArrayType), paramArrayIndex);
                    paramArrayIndex++;
                }
            } else {
                paramArray = Array.CreateInstance(paramArrayType, count);

                paramArray.SetValue(luaParamValue, 0);

                for (int i = 1; i < count; i++) {
                    object value = extractValue(Translator.State, startIndex);
                    paramArray.SetValue(value, i);
                    startIndex++;
                }

            }

            return paramArray;

        }

        /// <summary>
        /// Matches a method against its arguments in the Lua stack. Returns
        /// if the match was successful. It it was also returns the information
        /// necessary to invoke the method.
        /// </summary>
        internal bool MatchParameters(MethodBase method, LuaMethodCache methodCache, int skipParam) {
            ParameterInfo[] paramInfo = method.GetParameters();
            int currentLuaParam = 1;
            int nLuaParams = State.GetTop() - skipParam;
            List<object> paramList = new List<object>();
            List<int> outIndices = new List<int>();
            List<LuaMethodArg> argTypes = new List<LuaMethodArg>();

            // FIXME: NLua lacked support for type.Method(inst, ...) - this should help, but is it correct? -ade
            if (!method.IsStatic && skipParam == 1) {
                ExtractValue extractValue = Translator.Checker.CheckLuaType(Translator.State, currentLuaParam, method.DeclaringType);
                if (extractValue == null)
                    return false;

                nLuaParams++;
                object value = extractValue(Translator.State, currentLuaParam);
                paramList.Add(value);
                int index = paramList.Count - 1;
                LuaMethodArg methodArg = new LuaMethodArg();
                methodArg.Index = index;
                methodArg.ExtractValue = extractValue;
                methodArg.ParameterType = method.DeclaringType;
                argTypes.Add(methodArg);
                currentLuaParam++;
            }

            foreach (ParameterInfo currentNetParam in paramInfo) {
                // Skips out params 
                if (!currentNetParam.IsIn && currentNetParam.IsOut) {
                    paramList.Add(null);
                    outIndices.Add(paramList.Count - 1);
                    continue;
                }

                // TODO: The following comment comes from NLua. Figure out what it means. -ade
                // Type does not match, ignore if the parameter is optional

                if (IsParamsArray(nLuaParams, currentLuaParam, currentNetParam, out ExtractValue extractValue)) {
                    int count = (nLuaParams - currentLuaParam) + 1;
                    Type paramArrayType = currentNetParam.ParameterType.GetElementType();

                    Array paramArray = TableToArray(extractValue, paramArrayType, ref currentLuaParam, count);
                    paramList.Add(paramArray);
                    int index = paramList.LastIndexOf(paramArray);
                    LuaMethodArg methodArg = new LuaMethodArg();
                    methodArg.Index = index;
                    methodArg.ExtractValue = extractValue;
                    methodArg.IsParamsArray = true;
                    methodArg.ParameterType = paramArrayType;
                    argTypes.Add(methodArg);
                    continue;
                }

                // Adds optional parameters
                if (currentLuaParam > nLuaParams) {
                    if (!currentNetParam.IsOptional)
                        return false;
                    paramList.Add(currentNetParam.DefaultValue);
                    continue;
                }

                extractValue = Translator.Checker.CheckLuaType(Translator.State, currentLuaParam, currentNetParam.ParameterType);
                // Type checking
                if (extractValue != null) {
                    object value = extractValue(Translator.State, currentLuaParam);
                    paramList.Add(value);
                    int index = paramList.Count - 1;
                    LuaMethodArg methodArg = new LuaMethodArg();
                    methodArg.Index = index;
                    methodArg.ExtractValue = extractValue;
                    methodArg.ParameterType = currentNetParam.ParameterType;
                    argTypes.Add(methodArg);

                    if (currentNetParam.ParameterType.IsByRef)
                        outIndices.Add(index);

                    currentLuaParam++;
                    continue;
                }

                if (currentNetParam.IsOptional) {
                    paramList.Add(currentNetParam.DefaultValue);
                    continue;
                }

                return false;
            }

            // Number of parameters does not match
            if (currentLuaParam != nLuaParams + 1)
                return false;

            methodCache.Args = paramList.ToArray();
            methodCache.CachedMethod = method;
            methodCache.OutIndices = outIndices.ToArray();
            methodCache.ArgTypes = argTypes.ToArray();

            return true;
        }

        private bool IsParamsArray(int nLuaParams, int currentLuaParam, ParameterInfo currentNetParam, out ExtractValue extractValue) {
            extractValue = null;

            if (!currentNetParam.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any())
                return false;

            bool isParamArray = nLuaParams < currentLuaParam;

            LuaType luaType = State.Type(currentLuaParam);

            if (luaType == LuaType.Table) {
                extractValue = Translator.Checker.GetExtractor(typeof(LuaTable));
                if (extractValue != null)
                    return true;
            } else {
                Type paramElementType = currentNetParam.ParameterType.GetElementType();

                extractValue = Translator.Checker.CheckLuaType(Translator.State, currentLuaParam, paramElementType);

                if (extractValue != null)
                    return true;
            }
            return isParamArray;
        }
    }
}
