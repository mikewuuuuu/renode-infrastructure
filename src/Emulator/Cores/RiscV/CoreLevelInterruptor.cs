//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    public class CoreLevelInterruptor : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IRiscVTimeProvider
    {
        public CoreLevelInterruptor(Machine machine, long frequency, int numberOfTargets = 1)
        {
            this.timerFrequency = frequency;
            if(numberOfTargets < 1)
            {
                throw new ConstructionException("Invalid numberOfTargets: provided {numberOfTargets} but should be greater or equal to 1.");
            }
            for(var i = 0; i < numberOfTargets; i++)
            {
                var hartId = i;
                irqs[2 * hartId] = new GPIO();
                irqs[2 * hartId + 1] = new GPIO();

                var timer = new ComparingTimer(machine.ClockSource, timerFrequency, this, hartId.ToString(), enabled: true, eventEnabled: true);
                timer.CompareReached += () => irqs[2 * hartId + 1].Set(true);

                mTimers.Add(timer);
            }

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {
                    (long)Registers.MTimeLo, new DoubleWordRegister(this).WithValueField(0, 32, FieldMode.Read,
                                 valueProviderCallback: _ => (uint)mTimers[0].Value,
                                 writeCallback: (_, value) =>
                    {
                        var timerValue = mTimers[0].Value;
                        timerValue &= ~0xffffffffUL;
                        timerValue |= value;
                        foreach(var timer in mTimers)
                        {
                            timer.Value = timerValue;
                        }

                    })
                },
                {
                    (long)Registers.MTimeHi, new DoubleWordRegister(this).WithValueField(0, 32, FieldMode.Read,
                             valueProviderCallback: _ => (uint)(mTimers[0].Value >> 32),
                             writeCallback: (_, value) =>
                    {
                        var timerValue = mTimers[0].Value;
                        timerValue &= 0xffffffffUL;
                        timerValue |= (ulong)value << 32;
                        foreach(var timer in mTimers) 
                        {
                            timer.Value = timerValue;
                        }
                    })
                }
            };

            for(var hart = 0; hart < numberOfTargets; ++hart)
            {
                var hartId = hart;
                registersMap.Add((long)Registers.MSipHart0 + 4 * hartId, new DoubleWordRegister(this).WithFlag(0, writeCallback: (_, value) => { irqs[2 * hartId].Set(value); }));
                registersMap.Add((long)Registers.MTimeCmpHart0Lo + 8 * hartId, new DoubleWordRegister(this).WithValueField(0, 32, writeCallback: (_, value) =>
                {
                    var limit = mTimers[hartId].Compare;
                    limit &= ~0xffffffffUL;
                    limit |= value;

                    irqs[2 * hartId + 1].Set(false);
                    mTimers[hartId].Compare = limit;
                }));

                registersMap.Add((long)Registers.MTimeCmpHart0Hi + 8 * hartId, new DoubleWordRegister(this).WithValueField(0, 32, writeCallback: (_, value) =>
                {
                    var limit = mTimers[hartId].Compare;
                    limit &= 0xffffffffUL;
                    limit |= (ulong)value << 32;

                    irqs[2 * hartId + 1].Set(false);
                    mTimers[hartId].Compare = limit;
                }));
            }

            registers = new DoubleWordRegisterCollection(this, registersMap);

            Connections = new ReadOnlyDictionary<int, IGPIO>(irqs);
        }

        public void Reset()
        {
            registers.Reset();
            foreach(var irq in irqs.Values)
            {
                irq.Set(false);
            }
            foreach(var timer in mTimers)
            {
                timer.Reset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public ulong TimerValue => mTimers[0]?.Value ?? 0; // "?." returns "(ulong?)null" instead of "default(ulong)", thus "?? 0"

        public long Size => 0x10000;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private readonly DoubleWordRegisterCollection registers;
        private readonly Dictionary<int, IGPIO> irqs = new Dictionary<int, IGPIO>();
        private readonly List<ComparingTimer> mTimers = new List<ComparingTimer>();
        private readonly long timerFrequency;

        private enum Registers : long
        {
            MSipHart0 = 0x0,
            MTimeCmpHart0Lo = 0x4000,
            MTimeCmpHart0Hi = 0x4004,
            MTimeLo = 0xBFF8,
            MTimeHi = 0xBFFC
        }
    }
}
