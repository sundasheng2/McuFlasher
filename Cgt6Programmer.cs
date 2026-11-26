using System.Globalization;
using System.IO;

namespace McuFlasher;

internal static class Cgt6Programmer
{
    public const string VerifiedMessage = "CGT6 flash read-back verified";
    public const string PackName = "GigaDevice.GD32F30x_DFP.2.2.1.pack";
    public const string PythonLibraries = "pyocd-python313";

    public static IReadOnlyList<string> Create(Firmware image, int speed, bool reset, string? packPath = null)
    {
        packPath ??= Path.Combine(AppContext.BaseDirectory, PackName);
        if (!File.Exists(packPath))
            throw new InvalidDataException($"请将 {PackName} 放到上位机 EXE 所在目录。");
        return ["-u", "-B", "-c", Script, Path.Combine(AppContext.BaseDirectory, PythonLibraries), packPath,
            image.FileName, image.Address ?? "-",
            (speed * 1000).ToString(CultureInfo.InvariantCulture), reset ? "1" : "0", VerifiedMessage];
    }

    private const string Script = """
        import json
        import logging
        import struct
        import sys
        from pathlib import Path

        libraries, pack, filename, address, frequency, reset, success = sys.argv[1:]
        if sys.version_info[:2] == (3, 13) and struct.calcsize("P") == 8 and Path(libraries).is_dir():
            sys.path.insert(0, libraries)

        try:
            from intelhex import IntelHex
            from pyocd.core.exceptions import Error as ProbeError, FlashFailure
            from pyocd.core.helpers import ConnectHelper
            from pyocd.core.memory_map import FlashRegion
            from pyocd.core.session import Session
            from pyocd.core.target import Target
            from pyocd.flash.loader import FlashLoader
        except ImportError:
            sys.exit("Install pyOCD in the selected Python: python -m pip install pyocd==0.46.0")

        logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")

        class FlashOnly:
            def will_init_target(self, target, init_sequence):
                # The vendor pack's SVD has whitespace before its XML declaration.
                # Flash programming depends on the FLM, not the peripheral descriptions.
                init_sequence.remove_task("load_svd")

        def report_failure(target):
            for name, address in (("FMC_STAT0", 0x4002200c), ("FMC_STAT1", 0x4002204c),
                                  ("RCU_RSTSCK", 0x40021024)):
                try:
                    logging.error("%s=0x%08X", name, target.read32(address))
                except ProbeError as error:
                    logging.error("%s unavailable: %s", name, error)
            try:
                logging.error("Core state=%s, PC=0x%08X", target.get_state().name, target.read_core_register("pc"))
            except ProbeError as error:
                logging.error("Core state or PC unavailable: %s", error)

        def read_image(path, image_address):
            if image_address == "-":
                with open(path, encoding="utf-8-sig") as firmware:
                    data = IntelHex(firmware)
                return [(start, data.tobinstr(start=start, size=end-start)) for start, end in data.segments()]
            return [(int(image_address, 16), Path(path).read_bytes())]

        def program_image(session, spans):
            if not spans or any(start < 0x08000000 or start + len(data) > 0x08100000 for start, data in spans):
                raise ValueError("Firmware records are outside CGT6 main Flash.")
            loader = FlashLoader(session, chip_erase="sector", keep_unwritten=True)
            for start, expected in spans:
                loader.add_data(start, expected)
            loader.commit()
            target = session.target
            print("Verifying Flash via SWD...")
            for start, expected in spans:
                for offset in range(0, len(expected), 4096):
                    chunk = expected[offset:offset + 4096]
                    actual = bytes(target.read_memory_block8(start + offset, len(chunk)))
                    if actual != chunk:
                        raise ValueError(f"Flash verification failed in the block at 0x{start + offset:08X}.")

        def send(ok, **fields):
            print("\n@mcu:" + json.dumps(dict(ok=ok, **fields)))

        def serve(session, region):
            target = session.target
            flash = region.flash
            sizes = {}
            sectors = []
            for bank, start, end, size in ((0, 0x08000000, 0x08080000, 2048), (1, 0x08080000, 0x08100000, 4096)):
                for index, page in enumerate(range(start, end, size)):
                    info = flash.get_sector_info(page)
                    if info is None or info.base_addr != page or info.size != size:
                        raise ValueError(f"Invalid Flash sector geometry at 0x{page:08X}.")
                    sizes[page] = size
                    sectors.append(dict(bank=bank, index=index, address=page, size=size))
            send(True, sectors=sectors)
            for line in sys.stdin:
                try:
                    request = json.loads(line)
                    command = request["command"]
                    if command == "run":
                        target.reset()
                        send(True)
                        continue
                    if target.get_state() != Target.State.HALTED:
                        target.reset_and_halt(Target.ResetType.SYSRESETREQ)
                        if target.get_state() != Target.State.HALTED:
                            raise ValueError("Could not halt the connected CGT6 before the Flash operation.")
                    if command == "program":
                        program_image(session, read_image(request["path"], request["address"]))
                    elif command == "erase":
                        pages = sorted(set(request["addresses"]))
                        if not pages or any(page not in sizes for page in pages):
                            raise ValueError("Erase request contains an invalid sector.")
                        flash.init(flash.Operation.ERASE)
                        try:
                            for page in pages:
                                print(f"Erasing 0x{page:08X} bytes={sizes[page]}")
                                flash.erase_sector(page)
                        finally:
                            flash.cleanup()
                        for page in pages:
                            if any(value != 0xff for value in target.read_memory_block8(page, sizes[page])):
                                raise ValueError(f"Erase verification failed at 0x{page:08X}.")
                    else:
                        raise ValueError("Unknown flash session command.")
                    if target.get_state() != Target.State.HALTED:
                        target.halt()
                    if target.get_state() != Target.State.HALTED:
                        raise ValueError("Could not halt CGT6 after the Flash operation.")
                    send(True)
                except (ProbeError, OSError, ValueError, KeyError) as error:
                    if isinstance(error, FlashFailure):
                        report_failure(target)
                    send(False, error=str(error))
                    return

        probes = ConnectHelper.get_all_connected_probes(blocking=False, unique_id="stlink:")
        if len(probes) != 1:
            sys.exit(f"Connect exactly one ST-LINK. Detected: {len(probes)}")

        session = Session(probes[0], no_config=True, target_override="gd32f303cg", pack=pack,
                          frequency=int(frequency), dap_protocol="swd", auto_unlock=False,
                          resume_on_disconnect=False, **{"load.post_reset": "off"})
        session.delegate = FlashOnly()
        with session:
            target = session.target
            if (target.read32(0xe000ed00) & 0xfff0) != 0xc240 or target.read32(0x1ffff7e0) != 0x00600400:
                sys.exit("Expected GD32F303CGT6 with 1 MiB Flash and 96 KiB SRAM.")
            if (target.read32(0xe0042000) & 0xfff) != 0x430:
                sys.exit("Unexpected device ID. Check the target chip marking.")

            # The XD loader advertises uniform 2 KiB sectors. The GD32F30x manual
            # specifies 4 KiB erase pages above 0x08080000 on this exact device.
            region = target.memory_map.get_boot_memory()
            if (region is None or region.start != 0x08000000 or region.length != 0x100000
                    or region.has_subregions or region.flash is None or region.sector_size != 2048):
                sys.exit("Unexpected GD32F303CG flash geometry in the device pack.")
            region.submap.add_region(FlashRegion(
                name="Bank1", start=0x08080000, length=0x80000, sector_size=4096,
                page_size=region.page_size, erased_byte_value=region.erased_byte_value))

            # Stop at the reset handler before the installed application can start its watchdog.
            print("Preparing CGT6: system reset and halt before sector erase...")
            dbg_ctl = 0xe0042004
            watchdog_hold = 0x300
            original_hold = target.read32(dbg_ctl) & watchdog_hold
            target.write32(dbg_ctl, target.read32(dbg_ctl) | watchdog_hold)
            try:
                target.reset_and_halt(Target.ResetType.SYSRESETREQ)
                if target.get_state() != Target.State.HALTED:
                    sys.exit("Could not halt CGT6 at reset. Flash was not erased.")
                if filename == "--session":
                    serve(session, region)
                else:
                    spans = read_image(filename, address)
                    program_image(session, spans)
            except FlashFailure:
                report_failure(target)
                raise
            finally:
                try:
                    target.write32(dbg_ctl, (target.read32(dbg_ctl) & ~watchdog_hold) | original_hold)
                except ProbeError as error:
                    logging.warning("Could not restore the debug watchdog hold bits: %s", error)
            if reset == "1":
                target.reset()
        if filename != "--session":
            print(success)
        """;
}
