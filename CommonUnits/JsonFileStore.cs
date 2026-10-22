using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace PolyHubIpc
{
    public static class JsonFileStore
    {
        public static T Load<T>(string filePath) where T : class
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return serializer.ReadObject(fs) as T;
            }
        }

        public static T LoadOrCreate<T>(string filePath, Func<T> createDefault) where T : class
        {
            if (File.Exists(filePath))
            {
                try
                {
                    T loaded = Load<T>(filePath);
                    if (loaded != null)
                        return loaded;
                }
                catch
                {
                }
            }

            T value = createDefault();
            Save(filePath, value);
            return value;
        }

        public static void Save<T>(string filePath, T value) where T : class
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(fs, value);
            }
        }
    }
}