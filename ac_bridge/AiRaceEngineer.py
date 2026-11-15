# Assetto Corsa Python app: publishes the live field to AiRaceEngineer.
# Reads only through AC's ac/acsys API; never writes controls to the game.
# Compatible with Assetto Corsa's embedded Python 3.3.

import ac
import acsys
import mmap
import time
import struct

APP_NAME = "AiRaceEngineer"
MAP_NAME = "AiRaceEngineer_AcField"
MAGIC = 0x45524941  # "AIRE", little endian
VERSION = 1
MAX_CARS = 128
HEADER_SIZE = 64
CAR_SIZE = 288
MAP_SIZE = HEADER_SIZE + MAX_CARS * CAR_SIZE
UPDATE_INTERVAL = 0.10
TEXT_INTERVAL = 2.0

HEADER = struct.Struct("<8i2d2f")
SEQUENCE = struct.Struct("<i")
CAR = struct.Struct("<8i11f2h")

FLAG_CONNECTED = 1
FLAG_PIT_LANE = 2
FLAG_PIT_BOX = 4
FLAG_LAP_INVALID = 8
FLAG_FINISHED = 16
FLAG_AI = 32

DRIVER_OFFSET = 80
MODEL_OFFSET = 144
TYRE_OFFSET = 240

_cs = lambda name: getattr(getattr(acsys, "CS", None), name, None)
_lap = _cs("LapCount")
_best = _cs("BestLap")
_last = _cs("LastLap")
_lap_time = _cs("LapTime")
_spline = _cs("NormalizedSplinePosition")
_speed = _cs("SpeedMS")
_world = _cs("WorldPosition")
_velocity = _cs("Velocity")
_invalid = _cs("LapInvalidated")
_finished = _cs("RaceFinished")
_gas = _cs("Gas")
_brake = _cs("Brake")
_steer = _cs("Steer")
_gear = _cs("Gear")
_rpm = _cs("RPM")

_pit_lane = getattr(ac, "isCarInPitlane", None) or getattr(ac, "isCarInPitline", None)
_pit_box = getattr(ac, "isCarInPit", None)
_connected = getattr(ac, "isConnected", None)
_ai = getattr(ac, "isAIControlled", None)
_leaderboard = getattr(ac, "getCarLeaderboardPosition", None)
_realtime = getattr(ac, "getCarRealTimeLeaderboardPosition", None)
_tyre = getattr(ac, "getCarTyreCompound", None)

_mapping = None
_sequence = 0
_next_write = 0.0
_last_text = 0.0
_texts = {}
_track_length = 0.0
_online = 0
_label = None
_written = 0


def _number(value):
    try:
        value = float(value)
        if value != value or value == float("inf") or value == float("-inf"):
            return 0.0
        return value
    except Exception:
        return 0.0


def _integer(value, fallback=0):
    try:
        return int(value)
    except Exception:
        return fallback


def _call(function, car, fallback):
    if function is None:
        return fallback
    try:
        return function(car)
    except Exception:
        return fallback


def _state(car, channel):
    if channel is None:
        return 0
    try:
        return ac.getCarState(car, channel)
    except Exception:
        return 0


def _vec(value):
    try:
        return _number(value[0]), _number(value[1]), _number(value[2])
    except Exception:
        return 0.0, 0.0, 0.0


def _put_text(offset, chars, value):
    if not isinstance(value, str):
        value = ""
    data = value.encode("utf-16-le", "replace")[:(chars - 1) * 2]
    _mapping[offset:offset + chars * 2] = data + b"\x00" * (chars * 2 - len(data))


def _clear_car(car, offset):
    CAR.pack_into(_mapping, offset, car, 0, 0, 0, 0, 0, 0, 0,
                  0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0, 0)
    _put_text(offset + DRIVER_OFFSET, 32, "")
    _put_text(offset + MODEL_OFFSET, 48, "")
    _put_text(offset + TYRE_OFFSET, 16, "")


def _write_car(car, offset, refresh_text):
    connected = _integer(_call(_connected, car, 1)) == 1 or not _online
    if not connected:
        _clear_car(car, offset)
        _texts.pop(car, None)
        return False

    flags = FLAG_CONNECTED
    if _integer(_call(_pit_lane, car, 0)) == 1:
        flags |= FLAG_PIT_LANE
    if _integer(_call(_pit_box, car, 0)) == 1:
        flags |= FLAG_PIT_BOX
    if _integer(_state(car, _invalid)) == 1:
        flags |= FLAG_LAP_INVALID
    if _integer(_state(car, _finished)) == 1:
        flags |= FLAG_FINISHED
    if _integer(_call(_ai, car, 0)) == 1:
        flags |= FLAG_AI

    x, y, z = _vec(_state(car, _world))
    vx, vy, vz = _vec(_state(car, _velocity))
    CAR.pack_into(
        _mapping, offset, car, flags,
        _integer(_state(car, _lap)), _integer(_call(_leaderboard, car, -1)),
        _integer(_call(_realtime, car, -1)), _integer(_state(car, _best)),
        _integer(_state(car, _last)), _integer(_state(car, _lap_time)),
        _number(_state(car, _spline)), _number(_state(car, _speed)),
        x, y, z, vx, vy, vz,
        _number(_state(car, _gas)), _number(_state(car, _brake)), _number(_state(car, _steer)),
        _integer(_state(car, _gear)), max(0, min(65535, _integer(_state(car, _rpm)))))

    if refresh_text or car not in _texts:
        driver = _call(ac.getDriverName, car, "")
        model = _call(ac.getCarName, car, "")
        tyre = _call(_tyre, car, "")
        label = (driver, model, tyre)
        if _texts.get(car) != label:
            _texts[car] = label
            _put_text(offset + DRIVER_OFFSET, 32, driver)
            _put_text(offset + MODEL_OFFSET, 48, model)
            _put_text(offset + TYRE_OFFSET, 16, tyre)
    return True


def _open():
    global _mapping
    try:
        _mapping = mmap.mmap(-1, MAP_SIZE, MAP_NAME)
    except Exception as error:
        ac.log("AiRaceEngineer field bridge: " + repr(error))


def acMain(ac_version):
    global _label
    try:
        app = ac.newApp(APP_NAME)
        ac.setTitle(app, APP_NAME)
        ac.setSize(app, 260, 42)
        _label = ac.addLabel(app, "In attesa della sessione")
        ac.setPosition(_label, 8, 18)
    except Exception:
        _label = None
    _open()
    return APP_NAME


def acUpdate(deltaT):
    global _sequence, _next_write, _last_text, _online, _track_length, _written
    if _mapping is None:
        return
    now = getattr(time, "perf_counter", time.clock)()
    if now < _next_write:
        return
    _next_write = now + UPDATE_INTERVAL

    if now - _last_text >= TEXT_INTERVAL:
        _last_text = now
        try:
            _online = 1 if ac.getServerName() else 0
        except Exception:
            _online = 0
        if _track_length <= 0.0:
            try:
                _track_length = max(0.0, _number(ac.getTrackLength(0)))
            except Exception:
                pass

    try:
        count = max(0, min(MAX_CARS, _integer(ac.getCarsCount())))
    except Exception:
        count = 0
    try:
        focused = _integer(ac.getFocusedCar())
    except Exception:
        focused = 0

    odd = _sequence + 1
    HEADER.pack_into(_mapping, 0, MAGIC, VERSION, odd, count, focused, _online, 1, 0,
                     now, _track_length, 0.0, 0.0)

    written = 0
    for car in range(count):
        if _write_car(car, HEADER_SIZE + car * CAR_SIZE, now - _last_text < 0.001):
            written += 1
    _sequence += 2
    SEQUENCE.pack_into(_mapping, 8, _sequence)
    _written = written
    if _label is not None:
        try:
            ac.setText(_label, "Campo AC condiviso · " + str(written) + " auto")
        except Exception:
            pass


def acShutdown():
    global _mapping
    if _mapping is not None:
        try:
            SEQUENCE.pack_into(_mapping, 8, _sequence + 1)
            struct.pack_into("<i", _mapping, 24, 2)
            SEQUENCE.pack_into(_mapping, 8, _sequence + 2)
            _mapping.close()
        except Exception:
            pass
        _mapping = None
