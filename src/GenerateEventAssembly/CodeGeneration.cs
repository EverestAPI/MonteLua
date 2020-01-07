using System;
using System.Threading;
using System.Reflection;

using System.Reflection.Emit;
using System.Collections.Generic;
using NLua.Method;

namespace NLua {
    static class CodeGeneration {
        private static readonly object Lock = new object();

        private static readonly Dictionary<Type, LuaClassType> _classCollection = new Dictionary<Type, LuaClassType>();
        private static readonly Dictionary<Type, Type> _delegateCollection = new Dictionary<Type, Type>();

        private static Dictionary<Type, Type> eventHandlerCollection = new Dictionary<Type, Type>();
        private static Type eventHandlerParent = typeof(LuaEventHandler);
        private static Type delegateParent = typeof(LuaDelegate);
        private static Type classHelper = typeof(LuaClassHelper);
        private static AssemblyBuilder newAssembly;
        private static ModuleBuilder newModule;
        private static int luaClassNumber = 1;

        static CodeGeneration() {
            AssemblyName assemblyName = new AssemblyName();
            assemblyName.Name = "NLua_generatedcode";
            newAssembly = Thread.GetDomain().DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            newModule = newAssembly.DefineDynamicModule("NLua_generatedcode");
        }

        /// <summary>
        /// Generates an event handler that calls a Lua function
        /// </summary>
        private static Type GenerateEvent(Type eventHandlerType) {
            string typeName;
            lock (Lock) {
                typeName = "LuaGeneratedClass" + luaClassNumber;
                luaClassNumber++;
            }

            // Define a public class in the assembly, called typeName
            TypeBuilder myType = newModule.DefineType(typeName, TypeAttributes.Public, eventHandlerParent);

            // Defines the handler method. Its signature is void(object, <subclassofEventArgs>)
            Type[] paramTypes = new Type[2];
            paramTypes[0] = typeof(object);
            paramTypes[1] = eventHandlerType;
            Type returnType = typeof(void);
            MethodBuilder handleMethod = myType.DefineMethod("HandleEvent", MethodAttributes.Public | MethodAttributes.HideBySig, returnType, paramTypes);

            // Emits the IL for the method. It loads the arguments
            // and calls the handleEvent method of the base class
            ILGenerator generator = handleMethod.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            MethodInfo miGenericEventHandler = eventHandlerParent.GetMethod("HandleEvent");
            generator.Emit(OpCodes.Call, miGenericEventHandler);
            // returns
            generator.Emit(OpCodes.Ret);
            // creates the new type
            return myType.CreateType();
        }

        /// <summary>
        /// Generates a type that can be used for instantiating a delegate
        /// of the provided type, given a Lua function.
        /// </summary>
        private static Type GenerateDelegate(Type delegateType) {
            string typeName;
            lock (Lock) {
                typeName = "LuaGeneratedClass" + luaClassNumber;
                luaClassNumber++;
            }

            // Define a public class in the assembly, called typeName
            TypeBuilder myType = newModule.DefineType(typeName, TypeAttributes.Public, delegateParent);

            // Defines the delegate method with the same signature as the
            // Invoke method of delegateType
            MethodInfo invokeMethod = delegateType.GetMethod("Invoke");
            ParameterInfo[] paramInfo = invokeMethod.GetParameters();
            Type[] paramTypes = new Type[paramInfo.Length];
            Type returnType = invokeMethod.ReturnType;

            // Counts out and ref params, for use later
            int nOutParams = 0;
            int nOutAndRefParams = 0;

            for (int i = 0; i < paramTypes.Length; i++) {
                paramTypes[i] = paramInfo[i].ParameterType;

                if ((!paramInfo[i].IsIn) && paramInfo[i].IsOut)
                    nOutParams++;

                if (paramTypes[i].IsByRef)
                    nOutAndRefParams++;
            }

            int[] refArgs = new int[nOutAndRefParams];
            MethodBuilder delegateMethod = myType.DefineMethod("CallFunction", invokeMethod.Attributes, returnType, paramTypes);

            // Generates the IL for the method
            ILGenerator generator = delegateMethod.GetILGenerator();
            generator.DeclareLocal(typeof(object[])); // original arguments
            generator.DeclareLocal(typeof(object[])); // with out-only arguments removed
            generator.DeclareLocal(typeof(int[])); // indexes of out and ref arguments

            if (!(returnType == typeof(void)))  // return value
                generator.DeclareLocal(returnType);
            else
                generator.DeclareLocal(typeof(object));

            // Initializes local variables
            generator.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_0);
            generator.Emit(OpCodes.Ldc_I4, paramTypes.Length - nOutParams);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_1);
            generator.Emit(OpCodes.Ldc_I4, nOutAndRefParams);
            generator.Emit(OpCodes.Newarr, typeof(int));
            generator.Emit(OpCodes.Stloc_2);

            // Stores the arguments in the local variables
            for (int iArgs = 0, iInArgs = 0, iOutArgs = 0; iArgs < paramTypes.Length; iArgs++) {
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ldc_I4, iArgs);
                generator.Emit(OpCodes.Ldarg, iArgs + 1);

                if (paramTypes[iArgs].IsByRef) {
                    if (paramTypes[iArgs].GetElementType().IsValueType) {
                        generator.Emit(OpCodes.Ldobj, paramTypes[iArgs].GetElementType());
                        generator.Emit(OpCodes.Box, paramTypes[iArgs].GetElementType());
                    } else
                        generator.Emit(OpCodes.Ldind_Ref);
                } else {
                    if (paramTypes[iArgs].IsValueType)
                        generator.Emit(OpCodes.Box, paramTypes[iArgs]);
                }

                generator.Emit(OpCodes.Stelem_Ref);

                if (paramTypes[iArgs].IsByRef) {
                    generator.Emit(OpCodes.Ldloc_2);
                    generator.Emit(OpCodes.Ldc_I4, iOutArgs);
                    generator.Emit(OpCodes.Ldc_I4, iArgs);
                    generator.Emit(OpCodes.Stelem_I4);
                    refArgs[iOutArgs] = iArgs;
                    iOutArgs++;
                }

                if (paramInfo[iArgs].IsIn || (!paramInfo[iArgs].IsOut)) {
                    generator.Emit(OpCodes.Ldloc_1);
                    generator.Emit(OpCodes.Ldc_I4, iInArgs);
                    generator.Emit(OpCodes.Ldarg, iArgs + 1);

                    if (paramTypes[iArgs].IsByRef) {
                        if (paramTypes[iArgs].GetElementType().IsValueType) {
                            generator.Emit(OpCodes.Ldobj, paramTypes[iArgs].GetElementType());
                            generator.Emit(OpCodes.Box, paramTypes[iArgs].GetElementType());
                        } else
                            generator.Emit(OpCodes.Ldind_Ref);
                    } else {
                        if (paramTypes[iArgs].IsValueType)
                            generator.Emit(OpCodes.Box, paramTypes[iArgs]);
                    }

                    generator.Emit(OpCodes.Stelem_Ref);
                    iInArgs++;
                }
            }

            // Calls the callFunction method of the base class
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ldloc_1);
            generator.Emit(OpCodes.Ldloc_2);
            MethodInfo miGenericEventHandler = delegateParent.GetMethod("CallFunction");
            generator.Emit(OpCodes.Call, miGenericEventHandler);

            // Stores return value
            if (returnType == typeof(void)) {
                generator.Emit(OpCodes.Pop);
                generator.Emit(OpCodes.Ldnull);
            } else if (returnType.IsValueType) {
                generator.Emit(OpCodes.Unbox, returnType);
                generator.Emit(OpCodes.Ldobj, returnType);
            } else
                generator.Emit(OpCodes.Castclass, returnType);

            generator.Emit(OpCodes.Stloc_3);

            // Stores new value of out and ref params
            for (int i = 0; i < refArgs.Length; i++) {
                generator.Emit(OpCodes.Ldarg, refArgs[i] + 1);
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ldc_I4, refArgs[i]);
                generator.Emit(OpCodes.Ldelem_Ref);

                if (paramTypes[refArgs[i]].GetElementType().IsValueType) {
                    generator.Emit(OpCodes.Unbox, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Ldobj, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Stobj, paramTypes[refArgs[i]].GetElementType());
                } else {
                    generator.Emit(OpCodes.Castclass, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Stind_Ref);
                }
            }

            // Returns
            if (!(returnType == typeof(void)))
                generator.Emit(OpCodes.Ldloc_3);

            generator.Emit(OpCodes.Ret);
            return myType.CreateType(); // creates the new type
        }

        internal static void GetReturnTypesFromClass(Type klass, out Type[][] returnTypes) {
            MethodInfo[] classMethods = klass.GetMethods();
            returnTypes = new Type[classMethods.Length][];

            int i = 0;

            foreach (MethodInfo method in classMethods) {
                if (klass.IsInterface) {
                    GetReturnTypesFromMethod(method, out returnTypes[i]);
                    i++;
                } else if (!method.IsPrivate && !method.IsFinal && method.IsVirtual) {
                    GetReturnTypesFromMethod(method, out returnTypes[i]);
                    i++;
                }
            }
        }

        /// <summary>
        /// Generates an implementation of klass, if it is an interface, or
        /// a subclass of klass that delegates its virtual methods to a Lua table.
        /// </summary>
        public static void GenerateClass(Type klass, out Type newType, out Type[][] returnTypes) {

            string typeName;
            lock (Lock) {
                typeName = "LuaGeneratedClass" + luaClassNumber;
                luaClassNumber++;
            }

            TypeBuilder myType;
            // Define a public class in the assembly, called typeName
            if (klass.IsInterface)
                myType = newModule.DefineType(typeName, TypeAttributes.Public, typeof(object), new Type[] {
                    klass,
                    typeof(ILuaGeneratedType)
                });
            else
                myType = newModule.DefineType(typeName, TypeAttributes.Public, klass, new Type[] { typeof(ILuaGeneratedType) });

            // Field that stores the Lua table
            FieldBuilder luaTableField = myType.DefineField("__luaInterface_luaTable", typeof(LuaTable), FieldAttributes.Public);
            // Field that stores the return types array
            FieldBuilder returnTypesField = myType.DefineField("__luaInterface_returnTypes", typeof(Type[][]), FieldAttributes.Public);
            // Generates the constructor for the new type, it takes a Lua table and an array
            // of return types and stores them in the respective fields
            ConstructorBuilder constructor = myType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] {
                typeof(LuaTable),
                typeof(Type[][])
            });
            ILGenerator generator = constructor.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);

            if (klass.IsInterface)
                generator.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
            else
                generator.Emit(OpCodes.Call, klass.GetConstructor(Type.EmptyTypes));

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Stfld, luaTableField);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Stfld, returnTypesField);
            generator.Emit(OpCodes.Ret);
            // Generates overriden versions of the klass' public virtual methods
            MethodInfo[] classMethods = klass.GetMethods();
            returnTypes = new Type[classMethods.Length][];
            int i = 0;

            foreach (MethodInfo method in classMethods) {
                if (klass.IsInterface) {
                    GenerateMethod(myType, method, MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                        i, luaTableField, returnTypesField, false, out returnTypes[i]);
                    i++;
                } else {
                    if (!method.IsPrivate && !method.IsFinal && method.IsVirtual) {
                        GenerateMethod(myType, method, (method.Attributes | MethodAttributes.NewSlot) ^ MethodAttributes.NewSlot, i,
                            luaTableField, returnTypesField, true, out returnTypes[i]);
                        i++;
                    }
                }
            }

            // Generates an implementation of the luaInterfaceGetLuaTable method
            MethodBuilder returnTableMethod = myType.DefineMethod("LuaInterfaceGetLuaTable",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual, typeof(LuaTable), new Type[0]);
            myType.DefineMethodOverride(returnTableMethod, typeof(ILuaGeneratedType).GetMethod("LuaInterfaceGetLuaTable"));
            generator = returnTableMethod.GetILGenerator();
            generator.Emit(OpCodes.Ldfld, luaTableField);
            generator.Emit(OpCodes.Ret);
            newType = myType.CreateType(); // Creates the type
        }

        internal static void GetReturnTypesFromMethod(MethodInfo method, out Type[] returnTypes) {
            ParameterInfo[] paramInfo = method.GetParameters();
            Type[] paramTypes = new Type[paramInfo.Length];
            List<Type> returnTypesList = new List<Type>();

            // Counts out and ref parameters, for later use, 
            // and creates the list of return types
            int nOutParams = 0;
            int nOutAndRefParams = 0;
            Type returnType = method.ReturnType;
            returnTypesList.Add(returnType);

            for (int i = 0; i < paramTypes.Length; i++) {
                paramTypes[i] = paramInfo[i].ParameterType;

                if (!paramInfo[i].IsIn && paramInfo[i].IsOut) {
                    nOutParams++;
                }

                if (paramTypes[i].IsByRef) {
                    returnTypesList.Add(paramTypes[i].GetElementType());
                    nOutAndRefParams++;
                }
            }

            returnTypes = returnTypesList.ToArray();
        }

        /// <summary>
        /// Generates an overriden implementation of method inside myType that delegates
        /// to a function in a Lua table with the same name, if the function exists. If it
        /// doesn't the method calls the base method (or does nothing, in case of interface
        /// implementations).
        /// </summary>
        private static void GenerateMethod(TypeBuilder myType, MethodInfo method, MethodAttributes attributes, int methodIndex,
            FieldInfo luaTableField, FieldInfo returnTypesField, bool generateBase, out Type[] returnTypes) {
            ParameterInfo[] paramInfo = method.GetParameters();
            Type[] paramTypes = new Type[paramInfo.Length];
            List<Type> returnTypesList = new List<Type>();

            // Counts out and ref parameters, for later use, 
            // and creates the list of return types
            int nOutParams = 0;
            int nOutAndRefParams = 0;
            Type returnType = method.ReturnType;
            returnTypesList.Add(returnType);

            for (int i = 0; i < paramTypes.Length; i++) {
                paramTypes[i] = paramInfo[i].ParameterType;
                if (!paramInfo[i].IsIn && paramInfo[i].IsOut)
                    nOutParams++;

                if (paramTypes[i].IsByRef) {
                    returnTypesList.Add(paramTypes[i].GetElementType());
                    nOutAndRefParams++;
                }
            }

            int[] refArgs = new int[nOutAndRefParams];
            returnTypes = returnTypesList.ToArray();

            // Generates a version of the method that calls the base implementation
            // directly, for use by the base field of the table
            if (generateBase) {
                MethodBuilder baseMethod = myType.DefineMethod("__luaInterface_base_" + method.Name,
                    MethodAttributes.Private | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                    returnType, paramTypes);
                ILGenerator generatorBase = baseMethod.GetILGenerator();
                generatorBase.Emit(OpCodes.Ldarg_0);

                for (int i = 0; i < paramTypes.Length; i++)
                    generatorBase.Emit(OpCodes.Ldarg, i + 1);

                generatorBase.Emit(OpCodes.Call, method);

                if (returnType == typeof(void))
                    generatorBase.Emit(OpCodes.Pop);

                generatorBase.Emit(OpCodes.Ret);
            }

            // Defines the method
            MethodBuilder methodImpl = myType.DefineMethod(method.Name, attributes, returnType, paramTypes);

            // If it's an implementation of an interface tells what method it
            // is overriding
            if (myType.BaseType.Equals(typeof(object)))
                myType.DefineMethodOverride(methodImpl, method);

            ILGenerator generator = methodImpl.GetILGenerator();
            generator.DeclareLocal(typeof(object[])); // original arguments
            generator.DeclareLocal(typeof(object[])); // with out-only arguments removed
            generator.DeclareLocal(typeof(int[])); // indexes of out and ref arguments

            if (!(returnType == typeof(void))) // return value
                generator.DeclareLocal(returnType);
            else
                generator.DeclareLocal(typeof(object));

            // Initializes local variables
            generator.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_0);
            generator.Emit(OpCodes.Ldc_I4, paramTypes.Length - nOutParams + 1);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_1);
            generator.Emit(OpCodes.Ldc_I4, nOutAndRefParams);
            generator.Emit(OpCodes.Newarr, typeof(int));
            generator.Emit(OpCodes.Stloc_2);
            generator.Emit(OpCodes.Ldloc_1);
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, luaTableField);
            generator.Emit(OpCodes.Stelem_Ref);

            // Stores the arguments into the local variables, as needed
            for (int iArgs = 0, iInArgs = 1, iOutArgs = 0; iArgs < paramTypes.Length; iArgs++) {
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ldc_I4, iArgs);
                generator.Emit(OpCodes.Ldarg, iArgs + 1);

                if (paramTypes[iArgs].IsByRef) {
                    if (paramTypes[iArgs].GetElementType().IsValueType) {
                        generator.Emit(OpCodes.Ldobj, paramTypes[iArgs].GetElementType());
                        generator.Emit(OpCodes.Box, paramTypes[iArgs].GetElementType());
                    } else
                        generator.Emit(OpCodes.Ldind_Ref);
                } else {
                    if (paramTypes[iArgs].IsValueType)
                        generator.Emit(OpCodes.Box, paramTypes[iArgs]);
                }

                generator.Emit(OpCodes.Stelem_Ref);

                if (paramTypes[iArgs].IsByRef) {
                    generator.Emit(OpCodes.Ldloc_2);
                    generator.Emit(OpCodes.Ldc_I4, iOutArgs);
                    generator.Emit(OpCodes.Ldc_I4, iArgs);
                    generator.Emit(OpCodes.Stelem_I4);
                    refArgs[iOutArgs] = iArgs;
                    iOutArgs++;
                }

                if (paramInfo[iArgs].IsIn || (!paramInfo[iArgs].IsOut)) {
                    generator.Emit(OpCodes.Ldloc_1);
                    generator.Emit(OpCodes.Ldc_I4, iInArgs);
                    generator.Emit(OpCodes.Ldarg, iArgs + 1);

                    if (paramTypes[iArgs].IsByRef) {
                        if (paramTypes[iArgs].GetElementType().IsValueType) {
                            generator.Emit(OpCodes.Ldobj, paramTypes[iArgs].GetElementType());
                            generator.Emit(OpCodes.Box, paramTypes[iArgs].GetElementType());
                        } else
                            generator.Emit(OpCodes.Ldind_Ref);
                    } else {
                        if (paramTypes[iArgs].IsValueType)
                            generator.Emit(OpCodes.Box, paramTypes[iArgs]);
                    }

                    generator.Emit(OpCodes.Stelem_Ref);
                    iInArgs++;
                }
            }

            // Gets the function the method will delegate to by calling
            // the getTableFunction method of class LuaClassHelper
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, luaTableField);
            generator.Emit(OpCodes.Ldstr, method.Name);
            generator.Emit(OpCodes.Call, classHelper.GetMethod("GetTableFunction"));
            Label lab1 = generator.DefineLabel();
            generator.Emit(OpCodes.Dup);
            generator.Emit(OpCodes.Brtrue_S, lab1);
            // Function does not exist, call base method
            generator.Emit(OpCodes.Pop);

            if (!method.IsAbstract) {
                generator.Emit(OpCodes.Ldarg_0);

                for (int i = 0; i < paramTypes.Length; i++)
                    generator.Emit(OpCodes.Ldarg, i + 1);

                generator.Emit(OpCodes.Call, method);

                if (returnType == typeof(void))
                    generator.Emit(OpCodes.Pop);

                generator.Emit(OpCodes.Ret);
                generator.Emit(OpCodes.Ldnull);
            } else
                generator.Emit(OpCodes.Ldnull);

            Label lab2 = generator.DefineLabel();
            generator.Emit(OpCodes.Br_S, lab2);
            generator.MarkLabel(lab1);
            // Function exists, call using method callFunction of LuaClassHelper
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, returnTypesField);
            generator.Emit(OpCodes.Ldc_I4, methodIndex);
            generator.Emit(OpCodes.Ldelem_Ref);
            generator.Emit(OpCodes.Ldloc_1);
            generator.Emit(OpCodes.Ldloc_2);
            generator.Emit(OpCodes.Call, classHelper.GetMethod("CallFunction"));
            generator.MarkLabel(lab2);

            // Stores the function return value
            if (returnType == typeof(void)) {
                generator.Emit(OpCodes.Pop);
                generator.Emit(OpCodes.Ldnull);
            } else if (returnType.IsValueType) {
                generator.Emit(OpCodes.Unbox, returnType);
                generator.Emit(OpCodes.Ldobj, returnType);
            } else
                generator.Emit(OpCodes.Castclass, returnType);

            generator.Emit(OpCodes.Stloc_3);

            // Sets return values of out and ref parameters
            for (int i = 0; i < refArgs.Length; i++) {
                generator.Emit(OpCodes.Ldarg, refArgs[i] + 1);
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ldc_I4, refArgs[i]);
                generator.Emit(OpCodes.Ldelem_Ref);

                if (paramTypes[refArgs[i]].GetElementType().IsValueType) {
                    generator.Emit(OpCodes.Unbox, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Ldobj, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Stobj, paramTypes[refArgs[i]].GetElementType());
                } else {
                    generator.Emit(OpCodes.Castclass, paramTypes[refArgs[i]].GetElementType());
                    generator.Emit(OpCodes.Stind_Ref);
                }
            }

            // Returns
            if (!(returnType == typeof(void)))
                generator.Emit(OpCodes.Ldloc_3);

            generator.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Gets an event handler for the event type that delegates to the eventHandler Lua function.
        /// Caches the generated type.
        /// </summary>
        public static LuaEventHandler GetEvent(Type eventHandlerType, LuaFunction eventHandler) {
            Type eventConsumerType;

            if (eventHandlerCollection.ContainsKey(eventHandlerType))
                eventConsumerType = eventHandlerCollection[eventHandlerType];
            else {
                eventConsumerType = GenerateEvent(eventHandlerType);
                eventHandlerCollection[eventHandlerType] = eventConsumerType;
            }

            LuaEventHandler luaEventHandler = (LuaEventHandler) Activator.CreateInstance(eventConsumerType);
            luaEventHandler.Handler = eventHandler;
            return luaEventHandler;
        }

        /// <summary>
        /// Gets a delegate with delegateType that calls the luaFunc Lua function
        /// Caches the generated type.
        /// </summary>
        public static Delegate GetDelegate(Type delegateType, LuaFunction luaFunc) {
            List<Type> returnTypes = new List<Type>();
            Type luaDelegateType;

            if (_delegateCollection.ContainsKey(delegateType)) {
                luaDelegateType = _delegateCollection[delegateType];
            } else {
                luaDelegateType = GenerateDelegate(delegateType);
                _delegateCollection[delegateType] = luaDelegateType;
            }

            MethodInfo methodInfo = delegateType.GetMethod("Invoke");
            returnTypes.Add(methodInfo.ReturnType);

            foreach (ParameterInfo paramInfo in methodInfo.GetParameters()) {
                if (paramInfo.ParameterType.IsByRef)
                    returnTypes.Add(paramInfo.ParameterType);
            }

            LuaDelegate luaDelegate = (LuaDelegate) Activator.CreateInstance(luaDelegateType);
            luaDelegate.Function = luaFunc;
            luaDelegate.ReturnTypes = returnTypes.ToArray();
            return Delegate.CreateDelegate(delegateType, luaDelegate, "CallFunction");
        }

        /// <summary>
        /// Gets an instance of an implementation of the klass interface or
        /// subclass of klass that delegates public virtual methods to the
        /// luaTable table.
        /// Caches the generated type.
        /// </summary>
        public static object GetClassInstance(Type klass, LuaTable luaTable) {
            LuaClassType luaClassType;

            if (_classCollection.ContainsKey(klass))
                luaClassType = _classCollection[klass];
            else {
                luaClassType = new LuaClassType();
                GenerateClass(klass, out luaClassType.Type, out luaClassType.ReturnTypes);
                _classCollection[klass] = luaClassType;
            }

            return Activator.CreateInstance(luaClassType.Type, new object[] {
                luaTable,
                luaClassType.ReturnTypes
            });
        }
    }
}
