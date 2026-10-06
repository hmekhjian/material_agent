using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;

namespace MaterialAgent.RhinoSide
{
    /// <summary>
    /// Keeps real-world texture mapping on objects that get a Material Agent material through their layer:
    /// objects drawn on such a layer later, or moved onto it. Without this they would show the texture at
    /// Rhino's default (wrong) scale. Only touches objects that use the layer material and have no mapping on
    /// our channel yet, so it never overrides a mapping the user changed. Work is deferred to Idle, because
    /// objects must not be modified inside document events.
    /// </summary>
    public static class LayerAutoMapper
    {
        static readonly HashSet<(uint doc, Guid id)> Pending = new HashSet<(uint, Guid)>();
        static bool _hooked;

        public static void Enable()
        {
            if (_hooked) return;
            _hooked = true;
            RhinoDoc.AddRhinoObject += (s, e) => Queue(e.TheObject);
            RhinoDoc.ModifyObjectAttributes += (s, e) =>
            {
                if (e.OldAttributes.LayerIndex != e.NewAttributes.LayerIndex || e.OldAttributes.MaterialSource != e.NewAttributes.MaterialSource)
                    Queue(e.RhinoObject);
            };
            RhinoApp.Idle += (s, e) => Flush();
        }

        static void Queue(RhinoObject obj)
        {
            if (obj?.Document == null || obj.Attributes.MaterialSource != ObjectMaterialSource.MaterialFromLayer) return;
            lock (Pending) Pending.Add((obj.Document.RuntimeSerialNumber, obj.Id));
        }

        static void Flush()
        {
            List<(uint doc, Guid id)> work;
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                work = Pending.ToList();
                Pending.Clear();
            }

            foreach (var group in work.GroupBy(w => w.doc))
            {
                var doc = RhinoDoc.FromRuntimeSerialNumber(group.Key);
                if (doc == null) continue;
                foreach (var (_, id) in group)
                {
                    try { MapIfNeeded(doc, doc.Objects.FindId(id)); }
                    catch (Exception ex) { RhinoApp.WriteLine("Material Agent: could not map new object: " + ex.Message); }
                }
            }
        }

        static void MapIfNeeded(RhinoDoc doc, RhinoObject obj)
        {
            if (obj == null || obj.IsDeleted || obj.Attributes.MaterialSource != ObjectMaterialSource.MaterialFromLayer) return;
            if (obj.GetTextureChannels()?.Contains(MappingApplier.Channel) == true) return; // already mapped: leave it

            var layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
            var mapping = ProvenanceStore.MappingFrom(ProvenanceStore.Read(doc, layer?.RenderMaterial));
            if (mapping == null) return; // not ours, or a plain colour

            MappingApplier.Apply(doc, new[] { obj }, mapping);
        }
    }
}
