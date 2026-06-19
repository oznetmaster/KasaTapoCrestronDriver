using System.Threading;
using System.Threading.Tasks;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal enum ManagedLightKind
   {
   DimmableRoot,
   TunableWhiteRoot,
   ColorRoot,
   OnOffRoot,
   OnOffChild
   }

internal sealed class ManagedLightDescriptor
   {
   public ManagedLightDescriptor (
       string controllerId,
       string name,
       string modelName,
       string serialNumber,
       ManagedLightKind kind,
       string? childId = null)
      {
      ControllerId = controllerId;
      Name = name;
      ModelName = modelName;
      SerialNumber = serialNumber;
      Kind = kind;
      ChildId = childId;
      }

   public string ControllerId { get; }

   public string Name { get; }

   public string ModelName { get; }

   public string SerialNumber { get; }

   public ManagedLightKind Kind { get; }

   public string? ChildId { get; }
   }

internal interface IKasaManagedLightEntity
   {
   string DeviceName { get; }

   string ModelName { get; }

   string SerialNumber { get; }

   void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor);

   Task RefreshAsync (CancellationToken cancellationToken);

   void PublishStateSnapshot ();
   }