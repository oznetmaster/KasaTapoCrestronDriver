// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

[EntityDataType (Id = "platform:ManagedDevice")]
/// <summary>
/// Represents a managed child device entry exposed by the scaffold platform driver.
/// </summary>
public sealed class PlatformManagedDevice
   {
   /// <summary>
   /// Initializes a new instance of the <see cref="PlatformManagedDevice"/> class.
   /// </summary>
   /// <param name="uxCategory">The Crestron Home UX category used to render the managed device.</param>
   /// <param name="name">The display name shown for the managed device.</param>
   /// <param name="manufacturer">The manufacturer name reported for the managed device.</param>
   /// <param name="model">The model name reported for the managed device.</param>
   /// <param name="serialNumber">The unique serial number or identifier for the managed device.</param>
   public PlatformManagedDevice (DeviceUxCategory uxCategory, string name, string manufacturer, string model, string serialNumber)
      {
      UxCategory = uxCategory;
      Name = name;
      Manufacturer = manufacturer;
      Model = model;
      SerialNumber = serialNumber;
      }

   [EntityProperty]
   /// <summary>
   /// Gets the Crestron Home category used for the managed device UI representation.
   /// </summary>
   public DeviceUxCategory UxCategory { get; }

   [EntityProperty]
   /// <summary>
   /// Gets the display name of the managed device.
   /// </summary>
   public string Name { get; internal set; }

   [EntityProperty]
   /// <summary>
   /// Gets the manufacturer associated with the managed device.
   /// </summary>
   public string Manufacturer { get; }

   [EntityProperty]
   /// <summary>
   /// Gets the model associated with the managed device.
   /// </summary>
   public string Model { get; }

   [EntityProperty]
   /// <summary>
   /// Gets the serial number associated with the managed device.
   /// </summary>
   public string SerialNumber { get; }
   }
