#region References

using System;
using System.Runtime.InteropServices;
using Raspberry.IO.GeneralPurpose;
using Raspberry.Timers;

#endregion

namespace Raspberry.IO.InterIntegratedCircuit
{
    public class I2cDriver : IDisposable
    {
        #region Fields

        private readonly object driverLock = new object();

        private readonly ProcessorPin sdaPin;
        private readonly ProcessorPin sclPin;

        private readonly IntPtr gpioAddress;
        private readonly IntPtr bscAddress;

        private int currentDeviceAddress;
        private int waitInterval;

        #endregion

        #region Instance Management

        public I2cDriver(ProcessorPin sdaPin, ProcessorPin sclPin)
        {
            this.sdaPin = sdaPin;
            this.sclPin = sclPin;

            var bscBase = GetBscBase(sdaPin, sclPin);

            var memoryFile = Interop.open("/dev/mem", Interop.O_RDWR + Interop.O_SYNC);
            try
            {
                gpioAddress = Interop.mmap(IntPtr.Zero, Interop.BCM2835_BLOCK_SIZE, Interop.PROT_READ | Interop.PROT_WRITE, Interop.MAP_SHARED, memoryFile, Interop.BCM2835_GPIO_BASE);
                bscAddress = Interop.mmap(IntPtr.Zero, Interop.BCM2835_BLOCK_SIZE, Interop.PROT_READ | Interop.PROT_WRITE, Interop.MAP_SHARED, memoryFile, bscBase);
            }
            finally
            {
                Interop.close(memoryFile);
            }

            if (bscAddress == (IntPtr) Interop.MAP_FAILED)
                throw new InvalidOperationException();

            // Set the I2C pins to the Alt 0 function to enable I2C access on them
            SetPinMode((uint) (int) sdaPin, Interop.BCM2835_GPIO_FSEL_ALT0); // SDA
            SetPinMode((uint) (int) sclPin, Interop.BCM2835_GPIO_FSEL_ALT0); // SCL

            // Read the clock divider register
            var dividerAddress = bscAddress + (int) Interop.BCM2835_BSC_DIV;
            var divider = (ushort) SafeReadUInt32(dividerAddress);
            waitInterval = GetWaitInterval(divider);

            var addressAddress = bscAddress + (int)Interop.BCM2835_BSC_A;
            SafeWriteUInt32(addressAddress, (uint)currentDeviceAddress);
        }

        public void Dispose()
        {
            // Set all the I2C/BSC1 pins back to input
            SetPinMode((uint) (int) sdaPin, Interop.BCM2835_GPIO_FSEL_INPT); // SDA
            SetPinMode((uint) (int) sclPin, Interop.BCM2835_GPIO_FSEL_INPT); // SCL

            Interop.munmap(gpioAddress, Interop.BCM2835_BLOCK_SIZE);
            Interop.munmap(bscAddress, Interop.BCM2835_BLOCK_SIZE);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the clock divider.
        /// </summary>
        /// <value>
        /// The clock divider.
        /// </value>
        public int ClockDivider
        {
            get
            {
                var dividerAddress = bscAddress + (int) Interop.BCM2835_BSC_DIV;
                return (ushort) SafeReadUInt32(dividerAddress);
            }
            set
            {
                var dividerAddress = bscAddress + (int) Interop.BCM2835_BSC_DIV;
                SafeWriteUInt32(dividerAddress, (uint) value);

                var actualDivider = (ushort) SafeReadUInt32(dividerAddress);
                waitInterval = GetWaitInterval(actualDivider);
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Connects the specified device address.
        /// </summary>
        /// <param name="deviceAddress">The device address.</param>
        /// <returns>The device connection</returns>
        public I2cDeviceConnection Connect(int deviceAddress)
        {
            return new I2cDeviceConnection(this, deviceAddress);
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Writes the specified buffer.
        /// </summary>
        /// <param name="deviceAddress">The device address.</param>
        /// <param name="buffer">The buffer.</param>
        internal void Write(int deviceAddress, byte[] buffer)
        {
            lock (driverLock)
            {
                EnsureDeviceAddress(deviceAddress);

                var len = (uint) buffer.Length;

                var dlen = bscAddress + (int) Interop.BCM2835_BSC_DLEN;
                var fifo = bscAddress + (int) Interop.BCM2835_BSC_FIFO;
                var status = bscAddress + (int) Interop.BCM2835_BSC_S;
                var control = bscAddress + (int) Interop.BCM2835_BSC_C;

                var remaining = len;
                var i = 0;

                // Clear FIFO
                WriteUInt32Mask(control, Interop.BCM2835_BSC_C_CLEAR_1, Interop.BCM2835_BSC_C_CLEAR_1);

                // Clear Status
                WriteUInt32(status, Interop.BCM2835_BSC_S_CLKT | Interop.BCM2835_BSC_S_ERR | Interop.BCM2835_BSC_S_DONE);

                // Set Data Length
                WriteUInt32(dlen, len);

                // Enable device and start transfer
                WriteUInt32(control, Interop.BCM2835_BSC_C_I2CEN | Interop.BCM2835_BSC_C_ST);

                while ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_DONE) == 0)
                {
                    while ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_TXD) != 0 && remaining != 0)
                    {
                        // Write to FIFO, no barrier
                        WriteUInt32(fifo, buffer[i]);
                        i++;
                        remaining--;
                    }

                    Wait(remaining);
                }

                if ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_ERR) != 0) // Received a NACK
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_NACK");
                if ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_CLKT) != 0) // Received Clock Stretch Timeout
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_CLKT");
                if (remaining != 0) // Not all data is sent
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_DATA");

                WriteUInt32Mask(control, Interop.BCM2835_BSC_S_DONE, Interop.BCM2835_BSC_S_DONE);
            }
        }

        internal byte[] Read(int deviceAddress, int len)
        {
            lock (driverLock)
            {
                EnsureDeviceAddress(deviceAddress);

                var dlen = bscAddress + (int) Interop.BCM2835_BSC_DLEN;
                var fifo = bscAddress + (int) Interop.BCM2835_BSC_FIFO;
                var status = bscAddress + (int) Interop.BCM2835_BSC_S;
                var control = bscAddress + (int) Interop.BCM2835_BSC_C;

                var remaining = (uint) len;
                uint i = 0;

                // Clear FIFO
                WriteUInt32Mask(control, Interop.BCM2835_BSC_C_CLEAR_1, Interop.BCM2835_BSC_C_CLEAR_1);

                // Clear Status
                WriteUInt32(status, Interop.BCM2835_BSC_S_CLKT | Interop.BCM2835_BSC_S_ERR | Interop.BCM2835_BSC_S_DONE);

                // Set Data Length
                WriteUInt32(dlen, (uint) len);

                // Start read
                WriteUInt32(control, Interop.BCM2835_BSC_C_I2CEN | Interop.BCM2835_BSC_C_ST | Interop.BCM2835_BSC_C_READ);

                var buffer = new byte[len];
                while ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_DONE) == 0)
                {
                    Wait(remaining);

                    while ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_RXD) != 0)
                    {
                        // Read from FIFO, no barrier
                        buffer[i] = (byte) ReadUInt32(fifo);

                        i++;
                        remaining--;
                    }
                }

                if ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_ERR) != 0) // Received a NACK
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_NACK");
                if ((SafeReadUInt32(status) & Interop.BCM2835_BSC_S_CLKT) != 0) // Received Clock Stretch Timeout
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_CLKT");
                if (remaining != 0) // Not all data is received
                    throw new InvalidOperationException("BCM2835_I2C_REASON_ERROR_DATA");

                WriteUInt32Mask(control, Interop.BCM2835_BSC_S_DONE, Interop.BCM2835_BSC_S_DONE);

                return buffer;
            }
        }

        #endregion

        #region Private Helpers

        private void EnsureDeviceAddress(int deviceAddress)
        {
            if (deviceAddress != currentDeviceAddress)
            {
                var addressAddress = bscAddress + (int)Interop.BCM2835_BSC_A;
                SafeWriteUInt32(addressAddress, (uint)deviceAddress);

                currentDeviceAddress = deviceAddress;
            }
        }

        private void Wait(uint remaining)
        {
            // When remaining data is to be received, then wait for a fully FIFO
            Timer.Sleep(waitInterval * (remaining >= Interop.BCM2835_BSC_FIFO_SIZE ? Interop.BCM2835_BSC_FIFO_SIZE : remaining) / 1000m);
        }

        private static int GetWaitInterval(ushort actualDivider)
        {
            // Calculate time for transmitting one byte
            // 1000000 = micros seconds in a second
            // 9 = Clocks per byte : 8 bits + ACK

            return (int)((decimal)actualDivider * 1000000 * 9 / Interop.BCM2835_CORE_CLK_HZ);
        }

        private static uint GetBscBase(ProcessorPin sdaPin, ProcessorPin sclPin)
        {
            switch (Board.Current.Revision)
            {
                case 1:
                    if (sdaPin == ProcessorPin.Pin0 && sclPin == ProcessorPin.Pin1)
                        return Interop.BCM2835_BSC0_BASE;
                    throw new InvalidOperationException("I2C cannot be initialized for specified pins");

                case 2:
                    if (sdaPin == ProcessorPin.Pin2 && sclPin == ProcessorPin.Pin3)
                        return Interop.BCM2835_BSC1_BASE;
                    if (sdaPin == ProcessorPin.Pin28 && sclPin == ProcessorPin.Pin29)
                        return Interop.BCM2835_BSC0_BASE;
                    throw new InvalidOperationException("I2C cannot be initialized for specified pins");

                default:
                    throw new InvalidOperationException("Board revision not supported");
            }
        }

        private void SetPinMode(uint pin, uint mode)
        {
            // Function selects are 10 pins per 32 bit word, 3 bits per pin
            var paddr = gpioAddress + (int) (Interop.BCM2835_GPFSEL0 + 4*(pin/10));
            var shift = (pin%10)*3;
            var mask = Interop.BCM2835_GPIO_FSEL_MASK << (int) shift;
            var value = mode << (int) shift;

            WriteUInt32Mask(paddr, value, mask);
        }

        private static void WriteUInt32Mask(IntPtr address, uint value, uint mask)
        {
            var v = SafeReadUInt32(address);
            v = (v & ~mask) | (value & mask);
            SafeWriteUInt32(address, v);
        }

        private static uint SafeReadUInt32(IntPtr address)
        {
            // Make sure we dont return the _last_ read which might get lost
            // if subsequent code changes to a different peripheral
            var ret = ReadUInt32(address);
            ReadUInt32(address);

            return ret;
        }
        
        private static uint ReadUInt32(IntPtr address)
        {
            return (uint) Marshal.PtrToStructure(address, typeof (uint));
        }

        private static void SafeWriteUInt32(IntPtr address, uint value)
        {
            // Make sure we don't rely on the first write, which may get
            // lost if the previous access was to a different peripheral.
            WriteUInt32(address, value);
            WriteUInt32(address, value);
        }

        private static void WriteUInt32(IntPtr address, uint value)
        {
            Marshal.Copy(BitConverter.GetBytes(value), 0, address, 4);
        }

        #endregion
    }
}