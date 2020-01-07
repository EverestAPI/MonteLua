using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

using NLua.Exceptions;
using NLua.Extensions;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;

namespace NLua.Method {
    /// <summary>
    /// Argument extraction with type-conversion function
    /// </summary>
    delegate object ExtractValue(LuaState luaState, int stackPos);

    /// <summary>
    /// Wrapper class for methods/constructors accessed from Lua.
    /// </summary>
    class LuaMethodWrapper {
        internal LuaNativeFunction InvokeFunction;

        public readonly ObjectTranslator Translator;
        public LuaState State => Translator.State;
        internal MetaFunctions Meta => Translator.Meta;

        public readonly string MethodName;
        private readonly MethodInfo[] Matches;
        private readonly MethodBase SpecificMethod;

        private LuaMethodCache LastCalled;

        /// <summary>
        /// Constructs the wrapper for a known MethodBase instance
        /// </summary>
        public LuaMethodWrapper(ObjectTranslator translator, Type targetType, MethodBase method) {
            InvokeFunction = Call;
            Translator = translator;
            LastCalled = new LuaMethodCache();

            SpecificMethod = method;
            MethodName = method.Name;
        }

        /// <summary>
        /// Constructs the wrapper for a known method name
        /// </summary>
        public LuaMethodWrapper(ObjectTranslator translator, Type targetType, string methodName) {
            InvokeFunction = Call;

            Translator = translator;
            MethodName = methodName;
            LastCalled = new LuaMethodCache();

            Matches = GetMethodsRecursively(targetType, methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        }

        MethodInfo[] GetMethodsRecursively(Type type, string methodName, BindingFlags bindingType) {
            if (type == typeof(object))
                return type.GetMethods(methodName, bindingType);

            MethodInfo[] methods = type.GetMethods(methodName, bindingType);
            MethodInfo[] baseMethods = GetMethodsRecursively(type.BaseType, methodName, bindingType);

            return methods.Concat(baseMethods).ToArray();
        }

        /// <summary>
        /// Convert C# exceptions into Lua errors
        /// </summary>
        /// <returns>num of things on stack</returns>
        /// <param name="e">null for no pending exception</param>
        int SetPendingException(Exception e) {
            return Translator.Lua.SetPendingException(e);
        }

        void FillMethodArguments(int numStackToSkip) {
            object[] args = LastCalled.Args;

            for (int i = 0; i < LastCalled.ArgTypes.Length; i++) {
                LuaMethodArg type = LastCalled.ArgTypes[i];

                int index = i + 1 + numStackToSkip;

                if (LastCalled.ArgTypes[i].IsParamsArray) {
                    int count = LastCalled.ArgTypes.Length - i;
                    Array paramArray = Meta.TableToArray(type.ExtractValue, type.ParameterType, ref index, count);
                    args[LastCalled.ArgTypes[i].Index] = paramArray;
                } else {
                    args[type.Index] = type.ExtractValue(State, index);
                }

                if (LastCalled.Args[LastCalled.ArgTypes[i].Index] == null &&
                    !State.IsNil(i + 1 + numStackToSkip))
                    throw new LuaException(string.Format("Argument number {0} is invalid", (i + 1)));
            }
        }

        int PushReturnValue() {
            int nReturnValues = 0;
            // Pushes out and ref return values
            for (int index = 0; index < LastCalled.OutIndices.Length; index++) {
                nReturnValues++;
                Translator.Push(LastCalled.Args[LastCalled.OutIndices[index]]);
            }

            //  If not return void,we need add 1,
            //  or we will lost the function's return value 
            //  when call dotnet function like "int foo(arg1,out arg2,out arg3)" in Lua code 
            if (!LastCalled.IsReturnVoid && nReturnValues > 0)
                nReturnValues++;

            return nReturnValues < 1 ? 1 : nReturnValues;
        }

        int CallInvoke(MethodBase method) {
            if (!State.CheckStack(LastCalled.OutIndices.Length + 6))
                throw new LuaException("Lua stack overflow");

            try {
                object result;

                if (method.IsConstructor)
                    result = ((ConstructorInfo) method).Invoke(LastCalled.Args);
                else if (method.IsStatic)
                    result = method.Invoke(null, LastCalled.Args);
                else
                    result = method.Invoke(LastCalled.Args[0], LastCalled.Args.Skip(1).ToArray());

                Translator.Push(result);
            } catch (TargetInvocationException e) {
                // Failure of method invocation
                if (Translator.Lua.UseTraceback)
                    e.GetBaseException().Data["Traceback"] = Translator.Lua.GetDebugTraceback();
                return SetPendingException(e.GetBaseException());
            } catch (Exception e) {
                return SetPendingException(e);
            }

            return PushReturnValue();
        }

        bool IsMethodCached(int numArgsPassed) {
            if (LastCalled.CachedMethod == null)
                return false;

            if (numArgsPassed != LastCalled.ArgTypes.Length)
                return false;

            // If there is no method overloads, is ok to use the cached method
            if (Matches.Length == 1)
                return true;

            return Meta.MatchParameters(LastCalled.CachedMethod, LastCalled, 0);
        }

        int CallMethodFromName() {
            int numArgsPassed = State.GetTop();

            // Cached?
            if (IsMethodCached(numArgsPassed)) {
                MethodBase method = LastCalled.CachedMethod;

                if (!State.CheckStack(LastCalled.OutIndices.Length + 6))
                    throw new LuaException("Lua stack overflow");

                FillMethodArguments(0);

                return CallInvoke(method);
            }

            bool hasMatch = false;
            string candidateName = null;

            foreach (MethodInfo member in Matches) {
                if (member.ReflectedType == null)
                    continue;

                candidateName = member.ReflectedType.Name + "." + member.Name;

                if (Meta.MatchParameters(member, LastCalled, member.IsStatic ? 0 : 1)) {
                    hasMatch = true;
                    break;
                }
            }

            if (!hasMatch)
                return Translator.ThrowError(1, (candidateName == null) ? "Invalid arguments to method call" : ("Invalid arguments to method: " + candidateName));

            if (LastCalled.CachedMethod.ContainsGenericParameters)
                return CallInvokeOnGenericMethod((MethodInfo) LastCalled.CachedMethod);

            return CallInvoke(LastCalled.CachedMethod);
        }

        int CallInvokeOnGenericMethod(MethodInfo methodGen) {
            // need to make a concrete type of the generic method definition
            List<Type> typeArgs = new List<Type>();

            ParameterInfo[] parameters = methodGen.GetParameters();

            for (int i = 0; i < parameters.Length; i++) {
                ParameterInfo parameter = parameters[i];

                if (!parameter.ParameterType.IsGenericParameter)
                    continue;

                typeArgs.Add(LastCalled.Args[i].GetType());
            }

            MethodInfo method = methodGen.MakeGenericMethod(typeArgs.ToArray());

            object result;
            if (method.IsStatic)
                result = method.Invoke(null, LastCalled.Args);
            else
                result = method.Invoke(LastCalled.Args[0], LastCalled.Args.Skip(1).ToArray());
            Translator.Push(result);

            return PushReturnValue();
        }

        /// <summary>
        /// Calls the method. Receives the arguments from the Lua stack
        /// and returns values in it.
        /// </summary>
        int Call(IntPtr _) {
            MethodBase method = SpecificMethod;

            if (!State.CheckStack(5))
                throw new LuaException("Lua stack overflow");

            SetPendingException(null);

            // Method from name
            if (method == null)
                return CallMethodFromName();

            // Method from MethodBase instance
            if (!method.ContainsGenericParameters) {
                if (!Meta.MatchParameters(method, LastCalled, 0))
                    return Translator.ThrowError(1, "Invalid arguments to method call");
            } else {
                if (!method.IsGenericMethodDefinition)
                    return Translator.ThrowError(1, "Unable to invoke method on generic class as the current method is an open generic method");

                Meta.MatchParameters(method, LastCalled, 0);

                return CallInvokeOnGenericMethod((MethodInfo) method);
            }

            return CallInvoke(LastCalled.CachedMethod);
        }
    }
}
