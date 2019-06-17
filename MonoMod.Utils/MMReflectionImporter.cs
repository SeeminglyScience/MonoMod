﻿#if !CECIL0_9
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq.Expressions;
using MonoMod.Utils;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security;

#if NETSTANDARD
using TypeOrTypeInfo = System.Reflection.TypeInfo;
using static System.Reflection.IntrospectionExtensions;
using static System.Reflection.TypeExtensions;
#else
using TypeOrTypeInfo = System.Type;
#endif

namespace MonoMod.Utils {
    public sealed class MMReflectionImporter : IReflectionImporter {

        private class _Provider : IReflectionImporterProvider {
            public bool? UseDefault;
            public IReflectionImporter GetReflectionImporter(ModuleDefinition module) {
                MMReflectionImporter importer = new MMReflectionImporter(module);
                if (UseDefault != null)
                    importer.UseDefault = UseDefault.Value;
                return importer;
            }
        }

        public static readonly IReflectionImporterProvider Provider = new _Provider();
        public static readonly IReflectionImporterProvider ProviderNoDefault = new _Provider() { UseDefault = true };

        private readonly ModuleDefinition Module;
        private readonly DefaultReflectionImporter Default;

        private readonly Dictionary<AssemblyName, AssemblyNameReference> CachedAsms = new Dictionary<AssemblyName, AssemblyNameReference>();
        private readonly Dictionary<Type, TypeReference> CachedTypes = new Dictionary<Type, TypeReference>();
        private readonly Dictionary<FieldInfo, FieldReference> CachedFields = new Dictionary<FieldInfo, FieldReference>();
        private readonly Dictionary<MethodBase, MethodReference> CachedMethods = new Dictionary<MethodBase, MethodReference>();

        public bool UseDefault =
#if NETSTANDARD
            false;
#else
            false;
#endif

        private readonly Dictionary<Type, TypeReference> ElementTypes;

        public MMReflectionImporter(ModuleDefinition module) {
            Module = module;
            Default = new DefaultReflectionImporter(module);

            ElementTypes = new Dictionary<Type, TypeReference>() {
                { typeof(void), module.TypeSystem.Void },
                { typeof(bool), module.TypeSystem.Boolean },
                { typeof(char), module.TypeSystem.Char },
                { typeof(sbyte), module.TypeSystem.SByte },
                { typeof(byte), module.TypeSystem.Byte },
                { typeof(short), module.TypeSystem.Int16 },
                { typeof(ushort), module.TypeSystem.UInt16 },
                { typeof(int), module.TypeSystem.Int32 },
                { typeof(uint), module.TypeSystem.UInt32 },
                { typeof(long), module.TypeSystem.Int64 },
                { typeof(ulong), module.TypeSystem.UInt64 },
                { typeof(float), module.TypeSystem.Single },
                { typeof(double), module.TypeSystem.Double },
                { typeof(string), module.TypeSystem.String },
#if !NETSTANDARD1_X
                { typeof(TypedReference), module.TypeSystem.TypedReference },
#endif
                { typeof(IntPtr), module.TypeSystem.IntPtr },
                { typeof(UIntPtr), module.TypeSystem.UIntPtr },
                { typeof(object), module.TypeSystem.Object },
            };
        }

        public AssemblyNameReference ImportReference(AssemblyName asm) {
            if (CachedAsms.TryGetValue(asm, out AssemblyNameReference asmRef))
                return asmRef;

            return CachedAsms[asm] = Default.ImportReference(asm);
        }

        public TypeReference ImportReference(Type type, IGenericParameterProvider context) {
            if (CachedTypes.TryGetValue(type, out TypeReference typeRef))
                return typeRef;

            if (UseDefault)
                return CachedTypes[type] = Default.ImportReference(type, context);

            if (type.HasElementType) {
                if (type.IsByRef)
                    return CachedTypes[type] = new ByReferenceType(ImportReference(type.GetElementType(), context));

                if (type.IsPointer)
                    return CachedTypes[type] = new PointerType(ImportReference(type.GetElementType(), context));

                if (type.IsArray)
                    return CachedTypes[type] = new ArrayType(ImportReference(type.GetElementType(), context), type.GetArrayRank());
            }

            TypeOrTypeInfo typeInfo = type.GetTypeInfo();

            bool isGeneric = typeInfo.IsGenericType;
            if (isGeneric && !typeInfo.IsGenericTypeDefinition) {
                GenericInstanceType git = new GenericInstanceType(ImportReference(type.GetGenericTypeDefinition(), context));
                foreach (Type arg in type.GetGenericArguments())
                    git.GenericArguments.Add(ImportReference(arg, context));
                return git;
            }

            if (type.IsGenericParameter)
                return CachedTypes[type] = ImportGenericParameter(type, typeInfo, context);

            if (ElementTypes.TryGetValue(type, out typeRef))
                return CachedTypes[type] = typeRef;

            typeRef = new TypeReference(
				string.Empty,
				type.Name,
				Module,
				ImportReference(typeInfo.Assembly.GetName()),
                typeInfo.IsValueType
            );

            if (type.IsNested)
                typeRef.DeclaringType = ImportReference(type.DeclaringType, context);
            else if (type.Namespace != null)
                typeRef.Namespace = type.Namespace;

            if (typeInfo.IsGenericType)
                foreach (Type param in type.GetGenericArguments())
                    typeRef.GenericParameters.Add(new GenericParameter(param.Name, typeRef));

            return CachedTypes[type] = typeRef;
        }

        private static TypeReference ImportGenericParameter(Type type, TypeOrTypeInfo typeInfo, IGenericParameterProvider context) {
            if (context is MethodReference ctxMethodRef) {
                MethodBase dclMethod = typeInfo.DeclaringMethod;
                if (dclMethod != null) {
                    return ctxMethodRef.GenericParameters[type.GenericParameterPosition];
                } else {
                    context = ctxMethodRef.DeclaringType;
                }
            }

            Type dclType = type.DeclaringType;
            if (dclType == null)
                throw new NotSupportedException();

            if (context is TypeReference ctxTypeRef) {
                while (ctxTypeRef != null) {
                    // TODO: Possibly more lightweight type check!
                    if (!ctxTypeRef.Is(typeInfo)) {
                        ctxTypeRef = ctxTypeRef.DeclaringType;
                        continue;
                    }
                    return ctxTypeRef.GenericParameters[type.GenericParameterPosition];
                }
            }

            throw new NotSupportedException();
        }

        public FieldReference ImportReference(FieldInfo field, IGenericParameterProvider context) {
            if (CachedFields.TryGetValue(field, out FieldReference fieldRef))
                return fieldRef;

            if (UseDefault)
                return CachedFields[field] = Default.ImportReference(field, context);

            TypeReference declaringType = ImportReference(field.DeclaringType, context);
            return CachedFields[field] = new FieldReference(
                field.Name,
                ImportReference(field.FieldType, declaringType),
                declaringType
            );
        }

        public MethodReference ImportReference(MethodBase method, IGenericParameterProvider context) {
            if (CachedMethods.TryGetValue(method, out MethodReference methodRef))
                return methodRef;

            if (UseDefault)
                return CachedMethods[method] = Default.ImportReference(method, context);

            if (method.IsGenericMethod && !method.IsGenericMethodDefinition) {
                GenericInstanceMethod gim = new GenericInstanceMethod(ImportReference((method as MethodInfo).GetGenericMethodDefinition(), context));
                foreach (Type arg in method.GetGenericArguments())
                    // Generic arguments for the generic instance are often given by the next higher provider.
                    gim.GenericArguments.Add(ImportReference(arg, context));

                return CachedMethods[method] = gim;
            }

            methodRef = new MethodReference(
                method.Name,
                ImportReference(typeof(void), context),
                ImportReference(method.DeclaringType, context)
            );

            methodRef.HasThis = (method.CallingConvention & CallingConventions.HasThis) != 0;
            methodRef.ExplicitThis = (method.CallingConvention & CallingConventions.ExplicitThis) != 0;
            if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                methodRef.CallingConvention = MethodCallingConvention.VarArg;
            
            if (method.IsGenericMethodDefinition)
                foreach (Type param in method.GetGenericArguments())
                    methodRef.GenericParameters.Add(new GenericParameter(param.Name, methodRef));

            methodRef.ReturnType = ImportReference((method as MethodInfo)?.ReturnType ?? typeof(void), methodRef);

            foreach (ParameterInfo param in method.GetParameters())
                methodRef.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    (Mono.Cecil.ParameterAttributes) param.Attributes,
                    ImportReference(param.ParameterType, methodRef)
                ));

            return CachedMethods[method] = methodRef;
        }

    }
}
#endif
