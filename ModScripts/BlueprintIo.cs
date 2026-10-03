using ParallelTasks;
using Sandbox.ModAPI;
using System;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace CustomHangar
{
    /// <summary>
    /// Stored-grid file reads and writes on a background thread, so a large blueprint doesn't stall the
    /// server tick. The callback runs on the game thread (Parallel.RunCallbacks), where game state may change.
    /// Only the file I/O runs on the worker; it touches nothing in the world.
    /// </summary>
    public class BlueprintIo : WorkData
    {
        public string path;
        public MyObjectBuilder_Definitions blueprint;
        public bool success;
        Action<BlueprintIo> onDone;

        public static void Write(string path, MyObjectBuilder_Definitions blueprint, Action<BlueprintIo> onDone)
        {
            var work = new BlueprintIo { path = path, blueprint = blueprint, onDone = onDone };
            MyAPIGateway.Parallel.StartBackground(WriteAction, Done, work);
        }

        /// <summary>Reads a stored blueprint path (re-rooted on the current user-data folder); blueprint is null if unreadable.</summary>
        public static void Read(string storedPath, Action<BlueprintIo> onDone)
        {
            var work = new BlueprintIo { path = storedPath, onDone = onDone };
            MyAPIGateway.Parallel.StartBackground(ReadAction, Done, work);
        }

        static void WriteAction(WorkData data)
        {
            var work = (BlueprintIo)data;
            try
            {
                work.success = MyObjectBuilderSerializer.SerializeXML(work.path, false, work.blueprint);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Could not write stored grid {work.path}: {ex.Message}");
                work.success = false;
            }
        }

        static void ReadAction(WorkData data)
        {
            var work = (BlueprintIo)data;
            work.blueprint = Utils.LoadStoredBlueprint(work.path);
            work.success = work.blueprint != null;
        }

        static void Done(WorkData data)
        {
            var work = (BlueprintIo)data;
            if (Session.Instance == null) return; // world unloaded while the file was busy
            work.onDone?.Invoke(work);
        }
    }
}
