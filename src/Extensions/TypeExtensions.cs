using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NLua.Extensions {
    static class TypeExtensions {
        public static bool HasMethod(this Type t, string name) {
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            return methods.Any(m => m.Name == name);
        }

        public static MethodInfo[] GetMethods(this Type t, string name, BindingFlags flags) {
            return t.GetMethods(flags).Where(m => m.Name == name).ToArray();
        }

        private static MethodInfo[] _GetExtensionMethods(this Type type, string name, IEnumerable<Assembly> assemblies = null) {
            List<Type> types = new List<Type>();

            types.AddRange(type.Assembly.GetTypes().Where(t => t.IsPublic));

            if (assemblies != null) {
                foreach (Assembly item in assemblies) {
                    if (item == type.Assembly)
                        continue;
                    types.AddRange(item.GetTypes().Where(t => t.IsPublic && t.IsClass && t.IsSealed && t.IsAbstract && !t.IsNested));
                }
            }

            return
                types
                .SelectMany(
                    extensionType => extensionType.GetMethods(name, BindingFlags.Public | BindingFlags.Static),
                    (extensionType, method) => method
                )
                .Where(method => method.IsDefined(typeof(ExtensionAttribute), false))
                .Where(method =>
                    method.GetParameters()[0].ParameterType == type ||
                    method.GetParameters()[0].ParameterType.IsAssignableFrom(type) ||
                    type.GetInterfaces().Contains(method.GetParameters()[0].ParameterType)
                )
                .ToArray();
        }

        public static MethodInfo[] GetExtensionMethods(this Type t, string name, IEnumerable<Assembly> assemblies = null) {
            return t._GetExtensionMethods(name, assemblies).ToArray();
        }

        public static MethodInfo GetExtensionMethod(this Type t, string name, IEnumerable<Assembly> assemblies = null) {
            return t._GetExtensionMethods(name, assemblies).FirstOrDefault();
        }
    }
}
