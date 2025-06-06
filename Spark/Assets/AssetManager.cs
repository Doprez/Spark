﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Jitter.Dynamics;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;

namespace Spark
{
    public class AssetManager
    {
        public Device Device { get; private set; }
        public string BaseDirectory { get; set; }

        private readonly Dictionary<string, Asset> Assets = new Dictionary<string, Asset>();
        private static readonly Dictionary<Type, Dictionary<string, Type>> Importers = new Dictionary<Type, Dictionary<string, Type>>();
        //private static Dictionary<Type, Type> Exporters = new Dictionary<Type, Type>();

        public static List<string> GetExtensions(Type assetType)
        {
            var result = new List<string>();
            if(Importers.TryGetValue(assetType, out var table))
            {
                foreach (var pair in table)
                    result.Add(pair.Key);
            }
            return result;
        }

        public static List<string> GetAllExtensions(string prefix = null)
        {
            var result = new List<string>();

            foreach (var importer in Importers)
                foreach (var pair in importer.Value)
                    result.Add($"{prefix}{pair.Key}");

            return result;
        }

        static AssetManager()
        {
            CreateTables();
        }

        public AssetManager(Device device)
        {
            Device = device;
        }

        public void Dispose()
        {
            foreach(var pair in  Assets)
            {
                if(pair.Value is IDisposable disposable)
                    disposable.Dispose();
            }

            Assets.Clear();
        }

        private static void CreateTables()
        {
            Importers.Clear();
            //Exporters.Clear();

            Assembly assembly1 = Assembly.GetAssembly(typeof(AssetManager));
            Assembly assembly2 = Assembly.GetEntryAssembly();

            List<Type> types = new List<Type>();
            types.AddRange(assembly1.GetTypes());

            if (assembly1 != assembly2)
            {
                try
                {
                    types.AddRange(assembly2.GetTypes());
                }
                catch { }
            }

            foreach (Type type in types)
            {
                // importers
                var attributes = Attribute.GetCustomAttributes(type, typeof(AssetReaderAttribute));
                if (attributes.Length > 0)
                {
                    AssetReaderAttribute attribute = (AssetReaderAttribute)attributes[0];
                    MethodInfo method = type.GetMethod("Import");

                    if (!Importers.TryGetValue(method.ReturnType, out var importerTable))
                    {
                        importerTable = new Dictionary<string, Type>();
                        Importers.Add(method.ReturnType, importerTable);
                    }
                    foreach (string format in attribute.Formats)
                    {
                        if (!importerTable.TryGetValue(format.ToLower(), out _))
                            importerTable.Add(format.ToLower(), type);
                    }
                }

                // exporters
                //attrs = Attribute.GetCustomAttributes(type, typeof(ContentWriterAttribute));
                //if (attrs.Length > 0)
                //{
                //    ContentWriterAttribute attribute = (ContentWriterAttribute)attrs[0];
                //    MethodInfo method = type.GetMethod("Export");

                //    ParameterInfo[] param = method.GetParameters();

                //    if (!Tables.Exporters.ContainsKey(param[0].ParameterType))
                //        Tables.Exporters.Add(param[0].ParameterType, type);
                //}
            }
        }

        public static Type GetImporterType(string filename)
        {
            string extension = Path.GetExtension(filename).ToLower();

            foreach (var pair in Importers)
            {
                if (pair.Value.TryGetValue(extension, out var result))
                    return result;
            }

            return null;
        }

        public static Type GetAssetType(string filename)
        {
            string extension = Path.GetExtension(filename).ToLower();

            foreach(var pair in Importers)
            {
                if (pair.Value.ContainsKey(extension))
                    return pair.Key;
            }

            return null;
        }

        public Asset Load(string filename, params (string, object)[] arguments)
        {
            var type = GetAssetType(filename);
            if (type == null) return null;

            Type requested = type;

            var relativePath = filename;

            if (Assets.TryGetValue(filename, out var asset))
                return asset;

            if (type == typeof(Texture))
            {
                var withExtension = Path.GetFileName(filename);

                if (withExtension.StartsWith("@"))
                    filename = Path.Combine(AssetDatabase.Thumbnails.FullName, withExtension.Substring(1));
                else if (AssetDatabase.UsePackedAssets)
                {
                    var guid = AssetDatabase.PathToGuid(filename);
                    filename = Path.Combine(AssetDatabase.Packaged.FullName, guid + ".dds");
                }
                else filename = Path.Combine(BaseDirectory, filename);
            }
            else if (AssetDatabase.UsePackedAssets && type == typeof(Mesh))
            {
                var guid = AssetDatabase.PathToGuid(relativePath);
                filename = Path.Combine(AssetDatabase.Packaged.FullName, guid + ".smesh");
            }
            else filename = Path.Combine(BaseDirectory, filename);

            if (Assets.TryGetValue(filename, out asset))
                return asset;

            try
            {
                if (!Importers.TryGetValue(requested, out var importerTable))
                    throw new ArgumentOutOfRangeException("filename", "No content reader exists for the given type.");

                string extension = Path.GetExtension(filename).ToLower();

                if (!importerTable.TryGetValue(extension, out var importerType))
                    throw new ArgumentOutOfRangeException("filename", "No content reader exists for the given extension.");

                object importerInstance = Engine.IsEditor ? AssetDatabase.GetAssetReader(filename) : null;
                if (importerInstance == null) importerInstance = Activator.CreateInstance(importerType);

                if (arguments?.Length > 0)
                {
                    var mapping = Reflector.GetMapping(importerType);
                    foreach (var pair in arguments)
                        mapping.SetValue(pair.Item1, importerInstance, pair.Item2);
                }

                MethodInfo method = importerType.GetMethod("Import");
                if (!(method.Invoke(importerInstance, new object[1] { filename }) is Asset result))
                    return default;
                //throw new Exception("The content importer returned null.");

                result.Path = relativePath.Replace(Path.GetFullPath(BaseDirectory), "");
                result.Name = Path.GetFileNameWithoutExtension(relativePath);
                Assets.Add(filename, result);
                return result;
            }
            catch
            {
                return default;
            }
        } 


        public T Load<T>(string filename, params (string, object)[] arguments) where T : Asset
        {
            Type requested = typeof(T);

            var relativePath = filename;
            var type = GetAssetType(filename);

            if (Assets.TryGetValue(filename, out var asset))
                return (T)asset;

            if (type == typeof(Texture))
            {
                var withExtension = Path.GetFileName(filename);

                if (withExtension.StartsWith("@"))
                    filename = Path.Combine(AssetDatabase.Thumbnails.FullName, withExtension.Substring(1));
                else if (AssetDatabase.UsePackedAssets)
                {
                    var guid = AssetDatabase.PathToGuid(filename);
                    filename = Path.Combine(AssetDatabase.Packaged.FullName, guid + ".dds");
                }
                else filename = Path.Combine(BaseDirectory, filename);
            }
            else if (AssetDatabase.UsePackedAssets && type == typeof(Mesh))
            {
                var guid = AssetDatabase.PathToGuid(relativePath);
                filename = Path.Combine(AssetDatabase.Packaged.FullName, guid + ".smesh");
            }
            else filename = Path.Combine(BaseDirectory, filename);

            if (Assets.TryGetValue(filename, out asset))
                return (T)asset;

            try
            {
                if (!Importers.TryGetValue(requested, out var importerTable))
                    throw new ArgumentOutOfRangeException("filename", "No content reader exists for the given type.");

                string extension = Path.GetExtension(filename).ToLower();

                if (!importerTable.TryGetValue(extension, out var importerType))
                    throw new ArgumentOutOfRangeException("filename", "No content reader exists for the given extension.");

                object importerInstance = Engine.IsEditor ? AssetDatabase.GetAssetReader(filename) : null;
                if (importerInstance == null) importerInstance = Activator.CreateInstance(importerType);

                if (arguments?.Length > 0)
                {
                    var mapping = Reflector.GetMapping(importerType);
                    foreach (var pair in arguments)
                        mapping.SetValue(pair.Item1, importerInstance, pair.Item2);
                }

                MethodInfo method = importerType.GetMethod("Import");
                if (!(method.Invoke(importerInstance, new object[1] { filename }) is T result))
                    return default;
                    //throw new Exception("The content importer returned null.");

                result.Path = relativePath.Replace(Path.GetFullPath(BaseDirectory), "");
                result.Name = Path.GetFileNameWithoutExtension(relativePath);
                Assets.Add(filename, result);
                return result;
            }
            catch
            {
                return default;
            }
        }
    }
}