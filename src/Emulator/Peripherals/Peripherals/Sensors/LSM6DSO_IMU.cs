//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Diagnostics;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class LSM6DSO_IMU : BasicBytePeripheral, ISPIPeripheral, IProvidesRegisterCollection<ByteRegisterCollection>, ITemperatureSensor
    {
        public LSM6DSO_IMU(Machine machine) : base(machine)
        {
            Interrupt1 = new GPIO();

            commonFifo = new LSM6DSO_FIFO(machine, this, "fifo", MaxFifoWords);
            commonFifo.OnOverrun += UpdateInterrupts;
            temperatureFifo = new SensorSamplesFifo<ScalarSample>();

            DefineRegistersInternal();
            Reset();
        }

        public void FeedAccelerationSample(decimal x, decimal y, decimal z, uint repeat = 1)
        {
            for(var i = 0; i < repeat; i++)
            {
                commonFifo.FeedAccelerationSample(x, y, z);
            }
        }

        public void FeedAccelerationSample(string path)
        {
            commonFifo.FeedAccelerationSamplesFromFile(path);
        }

        public void FeedAccelerationSamplesFromBinaryFile(string path, string delayString = "0")
        {
            commonFifo.FeedAccelerationSamplesFromBinaryFile(path, delayString);
        }

        public void FeedAngularRateSample(decimal x, decimal y, decimal z, uint repeat = 1)
        {
            for(var i = 0; i < repeat; i++)
            {
                commonFifo.FeedAngularRateSample(x, y, z);
            }
        }

        public void FeedAngularRateSample(string path)
        {
            commonFifo.FeedAngularRateSamplesFromFile(path);
        }

        public void FeedAngularRateSamplesFromBinaryFile(string path, string delayString = "0")
        {
            commonFifo.FeedAngularRateSamplesFromBinaryFile(path, delayString);
        }

        public void FeedTemperatureSample(decimal value, uint repeat = 1)
        {
            var sample = new ScalarSample(value);

            for(var i = 0; i < repeat; i++)
            {
                temperatureFifo.FeedSample(sample);
            }
        }

        public void FeedTemperatureSample(string path)
        {
            temperatureFifo.FeedSamplesFromFile(path);
        }

        public void FeedTemperatureSamplesFromBinaryFile(string path, string delayString = "0")
        {
            temperatureFifo.FeedSamplesFromBinaryFile(machine, this, "temp", path, delayString);
        }

        public void FinishTransmission()
        {
            this.Log(LogLevel.Noisy, "Finishing transmission, going to the Idle state");
            commandInProgress = CommandTypes.None;
        }

        public override void Reset()
        {
            SoftwareReset();
        }

        public byte Transmit(byte data)
        {
            var value = (byte)0;
            // Datasheet page 20
            switch(commandInProgress)
            {
                case CommandTypes.None:
                    // The first transmission byte:
                    // b0-b6: address
                    // b7: 0 -- write; 1 -- read
                    address = BitHelper.GetValue(data, offset: 0, size: 7);
                    commandInProgress = (CommandTypes)BitHelper.GetValue(data, offset: IOTypeFlagPosition, size: 1);
                    this.Log(LogLevel.Noisy, "Received 0x{0:X2}; setting commandInProgress to {1} and address to 0x{2:X2}", data, commandInProgress, address);
                    break;
                case CommandTypes.Read:
                    value = ReadByte(address);
                    this.Log(LogLevel.Noisy, "Read from 0x{0:X2} ({1}): returning 0x{2:X2}", address, (Registers)address, value);
                    TryIncrementAddress();
                    break;
                case CommandTypes.Write:
                    this.Log(LogLevel.Noisy, "Write to 0x{0:X2} ({1}): 0x{2:X2}", address, (Registers)address, data);
                    WriteByte(address, data);
                    TryIncrementAddress();
                    break;
                default:
                    throw new ArgumentException($"Invalid commandInProgress: {commandInProgress}");
            }
            return value;
        }

        public bool FifoOverrunStatus => commonFifo.OverrunOccurred;
        public uint FifoWatermarkThreshold => fifoThresholdBits0_7.Value | (fifoThresholdBit8.Value ? 0x100u : 0x0u);
        public bool IsAccelerometerDataBatchedInFifo => IsDataRateEnabledAndDefined(accelerometerFifoBatchingDataRateSelection.Value);
        public bool IsAccelerometerPoweredOn => IsDataRateEnabledAndDefined(accelerometerOutputDataRateSelection.Value);
        public bool IsGyroscopeDataBatchedInFifo => IsDataRateEnabledAndDefined(gyroscopeFifoBatchingDataRateSelection.Value);
        public bool IsGyroscopePoweredOn => IsDataRateEnabledAndDefined(gyroscopeOutputDataRateSelection.Value, isGyroscopeOutputDataRate: true);
        public GPIO Interrupt1 { get; }

        public decimal DefaultAccelerationX 
        { 
            get => defaultAccelerationX;
            set
            {
                defaultAccelerationX = value;
                UpdateDefaultAccelerationSample();
            }
        }

        public decimal DefaultAccelerationY 
        { 
            get => defaultAccelerationY;
            set
            {
                defaultAccelerationY = value;
                UpdateDefaultAccelerationSample();
            }
        }

        public decimal DefaultAccelerationZ 
        { 
            get => defaultAccelerationZ;
            set
            {
                defaultAccelerationZ = value;
                UpdateDefaultAccelerationSample();
            }
        }

        public decimal DefaultAngularRateX
        {
            get => defaultAngularRateX;
            set
            {
                defaultAngularRateX = value;
                UpdateDefaultAngularRateSample();
            }
        }

        public decimal DefaultAngularRateY
        {
            get => defaultAngularRateY;
            set
            {
                defaultAngularRateY = value;
                UpdateDefaultAngularRateSample();
            }
        }

        public decimal DefaultAngularRateZ
        {
            get => defaultAngularRateZ;
            set
            {
                defaultAngularRateZ = value;
                UpdateDefaultAngularRateSample();
            }
        }

        // NOTE: The meaning of this field
        // is the default value of the channel
        // when there are no samples in the fifo
        public decimal Temperature
        {
            get => temperatureFifo.DefaultSample.Value;
            set
            {
                temperatureFifo.DefaultSample.Value = value;
            }
        }

        protected override void DefineRegisters()
        {
            // Intentionally left blank. This method is run from the base class constructor, i.e., before initializing necessary private objects.
        }

        private void DefineOutputRegistersGroup(Registers firstGroupRegister, string nameFormat, Func<LSM6DSO_Vector3DSample> sampleProvider)
        {
            var subsequentRegistersInfo = new SampleReadingRegisterInfo[]
            {
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.X, NameSuffix = "X_L", UpperByte = false},
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.X, NameSuffix = "X_H", UpperByte = true},
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.Y, NameSuffix = "Y_L", UpperByte = false},
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.Y, NameSuffix = "Y_H", UpperByte = true},
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.Z, NameSuffix = "Z_L", UpperByte = false},
                new SampleReadingRegisterInfo { Axis = LSM6DSO_Vector3DSample.Axes.Z, NameSuffix = "Z_H", UpperByte = true},
            };

            firstGroupRegister.DefineMany(this, 6, (register, registerOffset) =>
            {
                var registerInfo = subsequentRegistersInfo[registerOffset];
                var name = string.Format(nameFormat, registerInfo.NameSuffix);
                register.WithValueField(0, 8, FieldMode.Read, name: name,
                    valueProviderCallback: _ =>
                    {
                        var sample = sampleProvider();
                        if(sample == null)
                        {
                            return 0u;
                        }
                        Debug.Assert(sample.IsAccelerationSample || sample.IsAngularRateSample, $"Invalid sample: {sample}");

                        var sensitivity = sample.IsAccelerationSample ? accelerationSensitivity : angularRateSensitivity;
                        var _byte = sample.GetScaledValueByte(registerInfo.Axis, sensitivity, registerInfo.UpperByte, out var realScaledValue);

                        // Log only when reading the lower byte to avoid logging it twice for each value.
                        if(realScaledValue.HasValue && !registerInfo.UpperByte)
                        {
                            var fullScaleSelection = sample.IsAccelerationSample ? $"{GetAccelerationFullScaleValue()}G" : $"{GetAngularRateFullScaleValue()}DPS";
                            this.Log(LogLevel.Debug, "Invalid value for the current full scale selection ({0}): {1}", fullScaleSelection, realScaledValue.Value);
                        }
                        return _byte;
                    }
                );
            });
        }

        private void DefineRegistersInternal()
        {
            Registers.PinControl.Define(this)
                // These bits are always set.
                .WithValueField(0, 6, FieldMode.Read, valueProviderCallback: _ => 0x3F)
                .WithTaggedFlag("SDO_PU_EN", 6)
                .WithTaggedFlag("OIS_PU_DIS", 7)
                ;

            Registers.WhoAmI.Define(this)
                .WithValueField(0, 8, FieldMode.Read, name: "WHO_AM_I", valueProviderCallback: _ => 0x6C)
                ;

            Registers.Control1_Accelerometer.Define(this)
                .WithReservedBits(0, 1)
                .WithTaggedFlag("LPF2_XL_EN: Accelerometer high-resolution selection", 1)
                .WithEnumField(2, 2, out accelerationFullScaleSelection, name: "FS_XL: Accelerometer full-scale selection",
                    changeCallback: (_, __) => UpdateAccelerationSensitivity())
                .WithEnumField(4, 4, out accelerometerOutputDataRateSelection, name: "ODR_XL: Accelerometer Output Data Rate")
                ;

            Registers.Control2_Gyroscope.Define(this)
                .WithReservedBits(0, 1)
                .WithFlag(1, out angularRate125DPSSelection, name: "FS_125: Selects gyro UI chain full-scale 125 dps",
                    changeCallback: (_, __) => UpdateAngularRateSensitivity())
                .WithEnumField(2, 2, out angularRateFullScaleSelection, name: "FS_G: Gyroscope UI chain full-scale selection",
                    changeCallback: (_, __) => UpdateAngularRateSensitivity())
                .WithEnumField(4, 4, out gyroscopeOutputDataRateSelection, name: "ODR_G: Gyroscope Output Data Rate")
                ;

            Registers.Control3.Define(this, resetValue: 0b00000100)
                .WithFlag(0, FieldMode.WriteOneToClear, name: "SW_RESET", changeCallback: (_, newValue) => { if(newValue) SoftwareReset(); })
                .WithReservedBits(1, 1)
                .WithFlag(2, out addressAutoIncrement, name: "IF_INC")
                .WithTaggedFlag("SIM", 3)
                .WithTaggedFlag("PP_OD", 4)
                .WithTaggedFlag("H_LACTIVE", 5)
                .WithTaggedFlag("BDU", 6)
                .WithTaggedFlag("BOOT", 7)
                ;

            Registers.Control8_Accelerometer.Define(this)
                .WithTaggedFlag("LOW_PASS_ON_6D", 0)
                .WithFlag(1, out accelerationFullScaleMode, name: "XL_FS_MODE: Accelerometer full-scale management between UI chain and OIS chain",
                    changeCallback: (_, __) => UpdateAccelerationSensitivity())
                .WithTaggedFlag("HP_SLOPE_XL_EN", 2)
                .WithTaggedFlag("FASTSETTL_MODE_XL", 3)
                .WithTaggedFlag("HP_REF_MODE_XL", 4)
                .WithTag("HPCF_XL", 5, 3)
                ;

            Registers.TemperatureLow.Define(this)
                .WithValueField(0, 8, FieldMode.Read, name: "OUT_TEMP_L", valueProviderCallback: _ =>
                {
                    temperatureFifo.TryDequeueNewSample();
                    return GetScaledTemperatureValue(upperByte: false);
                })
                ;

            Registers.TemperatureHigh.Define(this)
                .WithValueField(0, 8, FieldMode.Read, name: "OUT_TEMP_H", valueProviderCallback: _ => GetScaledTemperatureValue(upperByte: true))
                ;

            Registers.FifoStatus1.Define(this)
                .WithValueField(0, 8, FieldMode.Read, name: "DIFF_FIFO_0-7", valueProviderCallback: _ => BitHelper.GetValue(commonFifo.SamplesCount, 0, 8))
                ;

            Registers.FifoStatus2.Define(this)
                .WithValueField(0, 2, FieldMode.Read, name: "DIFF_FIFO_8-9", valueProviderCallback: _ => BitHelper.GetValue(commonFifo.SamplesCount, 8, 2))
                .WithReservedBits(2, 1)
                // "This bit is reset when this register is read."
                .WithFlag(3, FieldMode.Read, name: "FIFO_OVR_LATCHED", valueProviderCallback: _ => FifoOverrunStatus && !previousFifoOverrunStatus)
                .WithTaggedFlag("COUNTER_BDR_IA", 4)
                // "FIFO will be full at the next Output Data Rate."
                // Let's check which sensor provider has both power and FIFO batching enabled. Providers are accelerometer and gyroscope.
                // Return true if there are more than 'MAX - (number of such providers)' samples in FIFO.
                .WithFlag(5, FieldMode.Read, name: "FIFO_FULL_IA", valueProviderCallback: _ =>
                {
                    var samplesAddedToFifoEveryODR = (IsAccelerometerDataBatchedInFifo && IsAccelerometerPoweredOn ? 1u : 0u) + (IsGyroscopeDataBatchedInFifo && IsGyroscopePoweredOn ? 1u : 0u);
                    return commonFifo.CountReached(MaxFifoWords - samplesAddedToFifoEveryODR);
                })
                .WithFlag(6, FieldMode.Read, name: "FIFO_OVR_IA", valueProviderCallback: _ => FifoOverrunStatus)
                .WithFlag(7, FieldMode.Read, name: "FIFO_WTM_IA", valueProviderCallback: _ => FifoWatermarkThreshold != 0 && commonFifo.CountReached(FifoWatermarkThreshold))
                .WithReadCallback((_, __) => previousFifoOverrunStatus = FifoOverrunStatus)
                ;

            Registers.FifoControl1.Define(this)
                .WithValueField(0, 8, out fifoThresholdBits0_7, name: "WTM0-7")
                ;

            Registers.FifoControl2.Define(this)
                .WithFlag(0, out fifoThresholdBit8, name: "WTM8")
                .WithTag("UNCOPTR_RATE", 1, 2)
                .WithReservedBits(3, 1)
                .WithTaggedFlag("ODRCHG_EN", 4)
                .WithReservedBits(5, 1)
                .WithTaggedFlag("FIFO_COMPR_RT_EN", 6)
                .WithTaggedFlag("STOP_ON_WTM", 7)
                ;

            Registers.FifoControl3.Define(this)
                .WithEnumField(0, 4, out accelerometerFifoBatchingDataRateSelection, name: "BDR_XL",
                    changeCallback: (_, __) =>
                    {
                        UpdateDefaultAccelerationSample();
                    }
                )
                .WithEnumField(4, 4, out gyroscopeFifoBatchingDataRateSelection, name: "BDR_GY",
                    changeCallback: (_, __) =>
                    {
                        UpdateDefaultAngularRateSample();
                    }
                )
                ;

            Registers.FifoControl4.Define(this)
                .WithEnumField<ByteRegister, FifoModes>(0, 3, name: "FIFO_MODE", writeCallback: (_, newValue) => commonFifo.Mode = newValue, valueProviderCallback: _ => commonFifo.Mode)
                .WithReservedBits(3, 1)
                .WithTag("ODR_T_BATCH", 4, 2)
                .WithTag("DEC_TS_BATCH", 6, 2)
                ;

            // New sample is dequeued when this register is read as it's the first "FifoOutput" register. There's no information on when samples are dequeued in the datasheet.
            Registers.FifoOutputTag.Define(this)
                .WithTaggedFlag("TAG_PARITY", 0)
                .WithTag("TAG_CNT", 1, 2)
                .WithEnumField<ByteRegister, FifoTag>(3, 5, FieldMode.Read, valueProviderCallback: _ => commonFifo.TryDequeueNewSample() ? commonFifo.Sample.Tag : FifoTag.UNKNOWN)
                ;

            DefineOutputRegistersGroup(Registers.AccelerometerXLow, "OUT{0}_A", () => commonFifo.AccelerationSample);
            DefineOutputRegistersGroup(Registers.FifoOutputXLow, "FIFO_DATA_OUT_{0}", () => commonFifo.Sample);
            DefineOutputRegistersGroup(Registers.GyroscopeXLow, "OUT{0}_G", () => commonFifo.AngularRateSample);

            Registers.Int1PinControl.Define(this)
                .WithTaggedFlag("INT1_DRDY_XL", 0)
                .WithTaggedFlag("INT1_DRDY_G", 1)
                .WithTaggedFlag("INT1_BOOT", 2)
                .WithTaggedFlag("INT1_TH", 3)
                .WithFlag(4, out interrupt1EnableFifoOverrun, name: "INT1_FIFO_OVR")
                .WithTaggedFlag("INT1_FULL", 5)
                .WithTaggedFlag("INT1_CNT_BDR", 6)
                .WithTaggedFlag("DEN_DRDY", 7)
                .WithWriteCallback((_, __) => UpdateInterrupts())
                ;
        }

        private short GetAccelerationFullScaleValue()
        {
            switch(accelerationFullScaleSelection.Value)
            {
                case AccelerationFullScaleSelection.Mode0_2G:
                    return 2;
                case AccelerationFullScaleSelection.Mode1_16G_2G:
                    return (short)(accelerationFullScaleMode.Value ? 2 : 16);
                case AccelerationFullScaleSelection.Mode2_4G:
                    return 4;
                case AccelerationFullScaleSelection.Mode3_8G:
                    return 8;
                default:
                    throw new Exception("Wrong acceleration full scale selection");
            }
        }

        // Sensitivity values come directly from the datasheet. The unit is 'mDPS/LSB' which is different than typical
        // 'LSB/DPS' or 'LSB/G' sensitivity is expected to be for proper conversions (e.g. in 'GetScaledValueByte').
        private decimal GetAngularRateSensitivityInMilliDPSPerLSB()
        {
            var fullScaleValue = GetAngularRateFullScaleValue();
            switch(fullScaleValue)
            {
                case 125:
                    return 4.375m;
                case 250:
                    return 8.75m;
                case 500:
                    return 17.5m;
                case 1000:
                    return 35m;
                case 2000:
                    return 70m;
                default:
                    throw new ArgumentException($"Invalid angular rate full scale value: {fullScaleValue}");
            }
        }

        private short GetAngularRateFullScaleValue()
        {
            if(angularRate125DPSSelection.Value)
            {
                return 125;
            }

            switch(angularRateFullScaleSelection.Value)
            {
                case AngularRateFullScaleSelection.Mode0_250DPS:
                    return 250;
                case AngularRateFullScaleSelection.Mode1_500DPS:
                    return 500;
                case AngularRateFullScaleSelection.Mode2_1000DPS:
                    return 1000;
                case AngularRateFullScaleSelection.Mode3_2000DPS:
                    return 2000;
                default:
                    throw new Exception("Wrong angular rate full scale selection");
            }
        }

        private byte GetScaledTemperatureValue(bool upperByte)
        {
            var scaled = (short)((temperatureFifo.Sample.Value - TemperatureOffset) * TemperatureSensitivity);
            return upperByte
                ? (byte)(scaled >> 8)
                : (byte)scaled;
        }

        private bool IsDataRateEnabledAndDefined(DataRates value, bool isGyroscopeOutputDataRate = false)
        {
            var enabled = value != DataRates.Disabled && Enum.IsDefined(typeof(DataRates), value);
            if(isGyroscopeOutputDataRate && enabled)
            {
                // This specific selection is invalid for the Gyroscope's Output Data Rate.
                enabled = value != DataRates._1_6HzOr6_5HzOr12_5Hz;
            }
            return enabled;
        }

        private void SoftwareReset()
        {
            address = 0x0;
            commandInProgress = CommandTypes.None;
            previousFifoOverrunStatus = false;

            commonFifo.Reset();
            temperatureFifo.Reset();

            base.Reset();

            UpdateAccelerationSensitivity();
            UpdateAngularRateSensitivity();
            UpdateInterrupts();
        }

        private void TryIncrementAddress()
        {
            if(!addressAutoIncrement.Value)
            {
                return;
            }

            // It's undocumented whether 0x0 should really be accessed after 0x7F.
            address = (byte)((address + 1) % 0x80);
        }

        private void UpdateAccelerationSensitivity()
        {
            accelerationSensitivity = decimal.Divide(MaxAbsoluteShortValue, GetAccelerationFullScaleValue());
        }

        private void UpdateAngularRateSensitivity()
        {
            // Gyroscope's sensitivity deviates from a typical '2**15 / full_scale' calculation.
            // Sensitivities in the datasheet are in milliDPS per LSB so let's convert to LSB per DPS.
            angularRateSensitivity = 1000m / GetAngularRateSensitivityInMilliDPSPerLSB();
        }

        private void UpdateInterrupts()
        {
            var newInt1Status = false;
            if(interrupt1EnableFifoOverrun.Value && FifoOverrunStatus)
            {
                newInt1Status = true;
            }

            if(Interrupt1.IsSet != newInt1Status)
            {
                this.Log(LogLevel.Debug, "New INT1 state: {0}", newInt1Status ? "set" : "reset");
                Interrupt1.Set(newInt1Status);
            }
        }

        private uint DataRateToFrequency(DataRates dr)
        {
            switch(dr)
            {
                // here we select the middle value, rounded down as currently we don't support fractions of Hz when declaring frequencies
                case DataRates._1_6HzOr6_5HzOr12_5Hz:
                    return 5;
                case DataRates._12_5Hz:
                    // here we need to round the value down as currently we don't support fractions of Hz when declaring frequencies
                    return 12;
                case DataRates._26Hz:
                    return 26;
                case DataRates._52Hz:
                    return 52;
                case DataRates._104Hz:
                    return 104;
                case DataRates._208Hz:
                    return 208;
                case DataRates._416Hz:
                    return 416;
                case DataRates._833Hz:
                    return 833;
                case DataRates._1_66kHz:
                    return 1660;
                case DataRates._3_33kHz:
                    return 3330;
                case DataRates._6_66kHz:
                    return 6660;
                default:
                    throw new Exception($"Unexpected data rate: {dr}");
            }
        }

        private IManagedThread accelerometerFeederThread;
        private IManagedThread gyroFeederThread;

        private decimal accelerationSensitivity;
        private byte address;
        private decimal angularRateSensitivity;
        private CommandTypes commandInProgress;
        private bool previousFifoOverrunStatus;

        private IEnumRegisterField<AccelerationFullScaleSelection> accelerationFullScaleSelection;
        private IFlagRegisterField accelerationFullScaleMode;
        private IEnumRegisterField<DataRates> accelerometerFifoBatchingDataRateSelection;
        private IEnumRegisterField<DataRates> accelerometerOutputDataRateSelection;
        private IFlagRegisterField addressAutoIncrement;
        private IFlagRegisterField angularRate125DPSSelection;
        private IEnumRegisterField<AngularRateFullScaleSelection> angularRateFullScaleSelection;
        private IValueRegisterField fifoThresholdBits0_7;
        private IFlagRegisterField fifoThresholdBit8;
        private IEnumRegisterField<DataRates> gyroscopeFifoBatchingDataRateSelection;
        private IEnumRegisterField<DataRates> gyroscopeOutputDataRateSelection;
        private IFlagRegisterField interrupt1EnableFifoOverrun;

        private readonly LSM6DSO_FIFO commonFifo;
        private readonly SensorSamplesFifo<ScalarSample> temperatureFifo;

        private const int IOTypeFlagPosition = 7;
        private const int MaxAbsoluteShortValue = 32768;
        private const uint MaxFifoWords = 512;
        private const short TemperatureOffset = 25;
        private const short TemperatureSensitivity = 256;

        private class LSM6DSO_FIFO : SensorSamplesFifo<LSM6DSO_Vector3DSample>
        {
            public LSM6DSO_FIFO(Machine machine, LSM6DSO_IMU owner, string name, uint capacity) : base()
            {
                Capacity = capacity;
                this.machine = machine;
                this.name = name;
                this.owner = owner;
            }

            public bool CountReached(uint value)
            {
                return SamplesCount >= value;
            }

            public void FeedAccelerationSample(decimal x, decimal y, decimal z)
            {
                var sample = new LSM6DSO_Vector3DSample(DefaultAccelerometerTag, x, y, z);
                FeedSample(sample);
            }

            public void FeedAccelerationSamplesFromFile(string path)
            {
                FeedSamplesFromFile(path, DefaultAccelerometerTag);
            }

            public void FeedAngularRateSample(decimal x, decimal y, decimal z)
            {
                var sample = new LSM6DSO_Vector3DSample(DefaultGyroscopeTag, x, y, z);
                FeedSample(sample);
            }

            public void FeedAngularRateSamplesFromFile(string path)
            {
                FeedSamplesFromFile(path, DefaultGyroscopeTag);
            }

            public override void FeedSample(LSM6DSO_Vector3DSample sample)
            {
                if(Mode == FifoModes.Bypass)
                {
                    // In bypass mode don't add samples to queue, just keep like the latest sample.
                    KeepSample(sample);
                    return;
                }

                if(Mode == FifoModes.Continuous && Full)
                {
                    if(!OverrunOccurred)
                    {
                        owner.Log(LogLevel.Debug, $"{name}: Overrun");
                        OverrunOccurred = true;
                        OnOverrun?.Invoke();
                    }

                    owner.Log(LogLevel.Noisy, $"{name}: Fifo filled up. Dumping the oldest sample.");
                    DumpOldestSample();
                }

                base.FeedSample(sample);
            }

            public void FeedAccelerationSamplesFromBinaryFile(string path, string delayString)
            {
                owner.accelerometerFeederThread?.Stop();
                owner.accelerometerFeederThread = FeedSamplesFromBinaryFile(path, delayString, DefaultAccelerometerTag, onFinish: () =>
                {
                    owner.UpdateDefaultAccelerationSample();
                });
            }

            public void FeedAngularRateSamplesFromBinaryFile(string path, string delayString)
            {
                owner.gyroFeederThread?.Stop();
                owner.gyroFeederThread = FeedSamplesFromBinaryFile(path, delayString, DefaultGyroscopeTag, onFinish: () =>
                {
                    owner.UpdateDefaultAngularRateSample();
                });;
            }

            public override void Reset()
            {
                owner.Log(LogLevel.Debug, "Resetting FIFO");

                accelerationSample = null;
                angularRateSample = null;
                mode = FifoModes.Bypass;
                OverrunOccurred = false;

                base.Reset();
            }

            public override bool TryDequeueNewSample()
            {
                if(CheckEnabled() && base.TryDequeueNewSample())
                {
                    owner.Log(LogLevel.Noisy, "New sample dequeued: {0}", Sample);
                    KeepSample(Sample);
                    return true;
                }
                owner.Log(LogLevel.Noisy, "Dequeueing new sample failed.");
                return false;
            }

            public LSM6DSO_Vector3DSample AccelerationSample => GetSample(DefaultAccelerometerTag);
            public LSM6DSO_Vector3DSample AngularRateSample => GetSample(DefaultGyroscopeTag);
            public bool Disabled => Mode == FifoModes.Bypass;
            public bool Empty => SamplesCount == 0;
            public bool Full => SamplesCount >= Capacity;

            public FifoModes Mode
            {
                get => mode;
                set
                {
                    if(value == FifoModes.Bypass)
                    {
                        Reset();
                    }
                    mode = value;
                }
            }

            public bool OverrunOccurred { get; private set; }
            public override LSM6DSO_Vector3DSample Sample => base.Sample.Tag != FifoTag.UNKNOWN ? base.Sample : null;

            public event Action OnOverrun;

            // Currently all Accelerometer/Gyroscope tags are treated the same so it doesn't matter which ones are default.
            public const FifoTag DefaultAccelerometerTag = FifoTag.AccelerometerNC;
            public const FifoTag DefaultGyroscopeTag = FifoTag.GyroscopeNC;

            private bool CheckEnabled()
            {
                if(Disabled)
                {
                    owner.Log(LogLevel.Debug, "Sample unavailable -- FIFO disabled.");
                    return false;
                }
                return true;
            }

            private void FeedSamplesFromFile(string path, FifoTag tag)
            {
                FeedSamplesFromFile(path,
                    stringArray =>
                    {
                        var sample = new LSM6DSO_Vector3DSample(tag);
                        return sample.TryLoad(stringArray) ? sample : null;
                    }
                );
            }

            private IManagedThread FeedSamplesFromBinaryFile(string path, string delayString, FifoTag tag, Action onFinish)
            {
                return FeedSamplesFromBinaryFile(machine, owner, name, path, delayString,
                    sampleConstructor: values =>
                    {
                        var sample = new LSM6DSO_Vector3DSample(tag);
                        sample.Load(values);
                        return sample;
                    },
                    onFinish: onFinish
                );
            }

            private LSM6DSO_Vector3DSample GetSample(FifoTag tag)
            {
                LSM6DSO_Vector3DSample sample;
                switch(tag)
                {
                    case DefaultAccelerometerTag:
                        sample = accelerationSample;
                        break;
                    case DefaultGyroscopeTag:
                        sample = angularRateSample;
                        break;
                    default:
                        throw new ArgumentException($"Tried to get sample for an unsupported tag: {tag}");
                }

                if(sample == null)
                {
                    owner.Log(LogLevel.Warning, "{0}: No sample found with {1} tag", name, tag);
                }
                return sample;
            }

            private void KeepSample(LSM6DSO_Vector3DSample sample)
            {
                if(sample.IsAccelerationSample)
                {
                    accelerationSample = sample;
                }
                else if(sample.IsAngularRateSample)
                {
                    angularRateSample = sample;
                }
                else
                {
                    throw new Exception($"Invalid sample to keep: tag={sample.Tag}");
                }
            }

            private uint Capacity { get; }

            private LSM6DSO_Vector3DSample accelerationSample;
            private LSM6DSO_Vector3DSample angularRateSample;
            private FifoModes mode;

            private readonly Machine machine;
            private readonly string name;
            private readonly LSM6DSO_IMU owner;
        }

        private class LSM6DSO_Vector3DSample : Vector3DSample
        {
            public LSM6DSO_Vector3DSample() : base()
            {
                // Intentionally left blank.
            }

            public LSM6DSO_Vector3DSample(FifoTag tag) : base()
            {
                Tag = tag;
            }

            public LSM6DSO_Vector3DSample(FifoTag tag, decimal x, decimal y, decimal z) : base()
            {
                Tag = tag;
                X = x;
                Y = y;
                Z = z;
            }

            public decimal GetAxisValue(Axes axis)
            {
                switch(axis)
                {
                    case Axes.X:
                        return X;
                    case Axes.Y:
                        return Y;
                    case Axes.Z:
                        return Z;
                    default:
                        throw new Exception($"Invalid Axis: {axis}");
                }
            }

            public byte GetScaledValueByte(Axes axis, decimal sensitivity, bool upperByte, out long? realScaledValue)
            {
                realScaledValue = null;
                var unscaledValue = GetAxisValue(axis);
                var scaledValue = (long)decimal.Round(unscaledValue * sensitivity, 0);
                short value;
                if(scaledValue > short.MaxValue)
                {
                    realScaledValue = scaledValue;
                    value = short.MaxValue;
                }
                else if(scaledValue < short.MinValue)
                {
                    realScaledValue = scaledValue;
                    value = short.MinValue;
                }
                else
                {
                    value = (short)scaledValue;
                }

                return upperByte
                    ? (byte)(value >> 8)
                    : (byte)value;
            }

            public override string ToString()
            {
                return $"[Tag: {Tag}, X: {X:F6}, Y: {Y:F6}, Z: {Z:F6}]";
            }

            public bool IsAccelerationSample { get; private set; }
            public bool IsAngularRateSample { get; private set; }

            public FifoTag Tag
            {
                get => tag;
                private set
                {
                    tag = value;
                    switch(tag)
                    {
                        case FifoTag.Accelerometer2xC:
                        case FifoTag.Accelerometer3xC:
                        case FifoTag.AccelerometerNC:
                        case FifoTag.AccelerometerNC_T_1:
                        case FifoTag.AccelerometerNC_T_2:
                            IsAccelerationSample = true;
                            break;
                        case FifoTag.Gyroscope2xC:
                        case FifoTag.Gyroscope3xC:
                        case FifoTag.GyroscopeNC:
                        case FifoTag.GyroscopeNC_T_1:
                        case FifoTag.GyroscopeNC_T_2:
                            IsAngularRateSample = true;
                            break;
                    }
                }
            }

            public enum Axes
            {
                X,
                Y,
                Z,
            }

            private FifoTag tag;
        }

        private void UpdateDefaultAccelerationSample()
        {
            accelerometerFeederThread?.Stop();
            if(accelerometerFifoBatchingDataRateSelection.Value != DataRates.Disabled)
            {
                var freq = DataRateToFrequency(accelerometerFifoBatchingDataRateSelection.Value);
                var defaultSample = new LSM6DSO_Vector3DSample(LSM6DSO_FIFO.DefaultAccelerometerTag, DefaultAccelerationX, DefaultAccelerationY, DefaultAccelerationZ);
                accelerometerFeederThread = commonFifo.FeedSamplesPeriodically(machine, this, "default_sample_acc", "0", freq, new [] { defaultSample });
                this.Log(LogLevel.Debug, "Started feeding default acceleration samples at {0}Hz", freq);
            }
            else
            {
                this.Log(LogLevel.Debug, "Stopped feeding default acceleration samples");
            }
        }

        private void UpdateDefaultAngularRateSample()
        {
            gyroFeederThread?.Stop();
            if(gyroscopeFifoBatchingDataRateSelection.Value != DataRates.Disabled)
            {
                var freq = DataRateToFrequency(gyroscopeFifoBatchingDataRateSelection.Value);
                var defaultSample = new LSM6DSO_Vector3DSample(LSM6DSO_FIFO.DefaultGyroscopeTag, DefaultAngularRateX, DefaultAngularRateY, DefaultAngularRateZ);
                gyroFeederThread = commonFifo.FeedSamplesPeriodically(machine, this, "default_sample_gyro", "0", freq, new [] { defaultSample });
                this.Log(LogLevel.Debug, "Started feeding default gyroscope samples at {0}Hz", freq);
            }
            else
            {
                this.Log(LogLevel.Debug, "Stopped feeding default gyroscope samples");
            }
        }

        private decimal defaultAccelerationX;
        private decimal defaultAccelerationY;
        private decimal defaultAccelerationZ;
        private decimal defaultAngularRateX;
        private decimal defaultAngularRateY;
        private decimal defaultAngularRateZ;

        private struct SampleReadingRegisterInfo
        {
            public LSM6DSO_Vector3DSample.Axes Axis;
            public string NameSuffix;
            public bool UpperByte;
        }

        private enum AccelerationFullScaleSelection : ushort
        {
            Mode0_2G = 0,
            Mode1_16G_2G = 1,
            Mode2_4G = 2,
            Mode3_8G = 3
        }

        private enum AngularRateFullScaleSelection : ushort
        {
            Mode0_250DPS = 0,
            Mode1_500DPS = 1,
            Mode2_1000DPS = 2,
            Mode3_2000DPS = 3
        }

        // Write == 0 and Read == 1 match the first transmission's byte R/W bit value.
        private enum CommandTypes
        {
            Write = 0,
            Read = 1,
            None,
        }

        private enum DataRates : ushort
        {
            Disabled = 0x0,
            _12_5Hz = 0x1,
            _26Hz = 0x2,
            _52Hz = 0x3,
            _104Hz = 0x4,
            _208Hz = 0x5,
            _416Hz = 0x6,
            _833Hz = 0x7,
            _1_66kHz = 0x8,
            _3_33kHz = 0x9,
            _6_66kHz = 0xA,
            _1_6HzOr6_5HzOr12_5Hz = 0xB,
        }

        private enum FifoModes : byte
        {
            Bypass = 0b000,
            StoppingFifo = 0b001,
            ContinuousToFifo = 0b011,
            BypassToContinuous = 0b100,
            Continuous = 0b110,
            BypassToFifo = 0b111,
        }

        private enum FifoTag : byte
        {
            UNKNOWN = 0x00,  // Undefined in the datasheet.
            GyroscopeNC = 0x01,
            AccelerometerNC = 0x02,
            Temperature = 0x03,
            Timestamp = 0x04,
            ConfigurationChange = 0x05,
            AccelerometerNC_T_2 = 0x06,
            AccelerometerNC_T_1 = 0x07,
            Accelerometer2xC = 0x08,
            Accelerometer3xC = 0x09,
            GyroscopeNC_T_2 = 0x0A,
            GyroscopeNC_T_1 = 0x0B,
            Gyroscope2xC = 0x0C,
            Gyroscope3xC = 0x0D,
            SensorHubSlave0 = 0x0E,
            SensorHubSlave1 = 0x0F,
            SensorHubSlave2 = 0x10,
            SensorHubSlave3 = 0x11,
            StepCounter = 0x12,
            SensorHubNack = 0x19
        }

        private enum Registers : byte
        {
            RegistersAccessConfiguration = 0x01,
            PinControl = 0x02,
            // Reserved: 0x03 - 0x06
            FifoControl1 = 0x07,
            FifoControl2 = 0x08,
            FifoControl3 = 0x09,
            FifoControl4 = 0x0A,
            CounterBatchDataRate1 = 0x0B,
            CounterBatchDataRate2 = 0x0C,
            Int1PinControl = 0x0D,
            Int2PinControl = 0x0E,
            WhoAmI = 0x0F,
            Control1_Accelerometer = 0x10,
            Control2_Gyroscope = 0x11,
            Control3 = 0x12,
            Control4 = 0x13,
            Control5 = 0x14,
            Control6 = 0x15,
            Control7_Gyroscope = 0x16,
            Control8_Accelerometer = 0x17,
            Control9_Accelerometer = 0x18,
            Control10 = 0x19,
            AllInterruptSource = 0x1A,
            WakeUpInterruptSource = 0x1B,
            TapInterruptSource = 0x1C,
            PositionInterruptSource = 0x1D,
            Status = 0x1E,  // Works differently when read by the auxiliary SPI.
            // Reserved: 0x1F
            TemperatureLow = 0x20,
            TemperatureHigh = 0x21,
            GyroscopeXLow = 0x22,
            GyroscopeXHigh = 0x23,
            GyroscopeYLow = 0x24,
            GyroscopeYHigh = 0x25,
            GyroscopeZLow = 0x26,
            GyroscopeZHigh = 0x27,
            AccelerometerXLow = 0x28,
            AccelerometerXHigh = 0x29,
            AccelerometerYLow = 0x2A,
            AccelerometerYHigh = 0x2B,
            AccelerometerZLow = 0x2C,
            AccelerometerZHigh = 0x2D,
            // Reserved: 0x2E - 0x34
            EmbeddedFunctionInterruptStatus = 0x35,
            FiniteStateMachineInterruptStatusA = 0x36,
            FiniteStateMachineInterruptStatusB = 0x37,
            // Reserved: 0x38
            SensorHubInterruptStatus = 0x39,
            FifoStatus1 = 0x3A,
            FifoStatus2 = 0x3B,
            // Reserved: 0x3C - 0x3F
            Timestamp0 = 0x40,
            Timestamp1 = 0x41,
            Timestamp2 = 0x42,
            Timestamp3 = 0x43,
            // Reserved: 0x44 - 0x55
            TapConfiguration0 = 0x56,
            TapConfiguration1 = 0x57,
            TapConfiguration2 = 0x58,
            TapThreshold6D = 0x59,
            IntervalDuration2 = 0x5A,
            WakeUpThreshold = 0x5B,
            WakeUpDuration = 0x5C,
            FreeFallDuration = 0x5D,
            Int1FunctionRouting = 0x5E,
            Int2FunctionRouting = 0x5F,
            // Reserved: 0x60 - 0x61
            I3CBusAvailable = 0x62,
            InternalFrequencyFine = 0x63,
            // Reserved: 0x64 - 0x6E
            OISInterrupt = 0x6F,
            OISControl1 = 0x70,
            OISControl2 = 0x71,
            OISControl3 = 0x72,
            AccelerometerUserOffsetX = 0x73,
            AccelerometerUserOffsetY = 0x74,
            AccelerometerUserOffsetZ = 0x75,
            // Reserved: 0x76 - 0x77
            FifoOutputTag = 0x78,
            FifoOutputXLow = 0x79,
            FifoOutputXHigh = 0x7A,
            FifoOutputYLow = 0x7B,
            FifoOutputYHigh = 0x7C,
            FifoOutputZLow = 0x7D,
            FifoOutputZHigh = 0x7E,
        }
    }
}
