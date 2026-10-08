# PLC interface — Modbus TCP register map

Generated from `config/tags.json` by `build/tools/make_plc_doc.py`. Do not edit by hand.

- PC = Modbus TCP **client**, PLC = **server** at `192.168.1.10:502`, unit id 1.
- PC reads holding registers with FC03 every 250 ms and writes with FC16. Addresses are 0-based (`40001` = address 0).
- `PLC_HEARTBEAT` must change at least every 5000 ms, otherwise the PC raises *PLC communication lost*.
- The PLC should stop scanning / lock parameters when `PC_HEARTBEAT` stops changing (PC down) and should only allow HMI parameter changes / Start when `LOGIN_LEVEL` > 0.

## PLC → PC (PLC writes, PC reads)

| Address | Bit | Tag | Type | Category | Unit / range | Meaning (vi) | Meaning (en) |
|---|---|---|---|---|---|---|---|
| 0 |  | `PLC_HEARTBEAT` | UInt16 | Internal |  | Nhịp PLC | PLC heartbeat |
| 1 |  | `MACHINE_RUNNING` | Bool | State |  | Trạng thái máy chạy | Machine running |
| 2 |  | `ESTOP_ACTIVE` | Bool | Alarm (alarm when == 1) |  | Dừng khẩn cấp | Emergency stop |
| 3 | 0 | `ALM_INVERTER` | Bool | Alarm (alarm when == 1) |  | Lỗi biến tần | Inverter fault |
| 3 | 1 | `ALM_TURNTABLE` | Bool | Alarm (alarm when == 1) |  | Lỗi lệch mâm xoay | Turntable misalignment |
| 3 | 2 | `ALM_AIR_PRESSURE` | Bool | Alarm (alarm when == 1) |  | Lỗi áp suất | Air pressure fault |
| 3 | 3 | `ALM_MAINTENANCE` | Bool | Alarm (alarm when == 1) |  | Cảnh báo bảo trì máy | Maintenance due |
| 3 | 4 | `ALM_INDATE_TEMP_HIGH` | Bool | Alarm (alarm when == 1) |  | Nhiệt độ In date cao | In-date temperature high |
| 3 | 5 | `ALM_INDATE_TEMP_LOW` | Bool | Alarm (alarm when == 1) |  | Nhiệt độ In date thấp | In-date temperature low |
| 4 | 0 | `LEAFLET_FEED_ENABLED` | Bool | State |  | Cho phép cấp toa | Leaflet feed enabled |
| 4 | 1 | `BOX_SUCTION_ENABLED` | Bool | State |  | Cho phép hút hộp | Box suction enabled |
| 4 | 2 | `INDATE_ON` | Bool | State |  | Bật In date | In-date printer on |
| 5 |  | `SPEED_ACTUAL` | UInt16 | Process | hộp/phút | Tốc độ thực tế | Actual speed |
| 6 |  | `TEMP_ACTUAL` | Int16 | Process | °C | Nhiệt độ thực tế | Actual temperature |
| 10 |  | `CAM_HUT_HOP_ON_ACT` | UInt16 | Parameter | ° | Cam Hút hộp ON | Cam Box suction ON |
| 11 |  | `CAM_HUT_HOP_OFF_ACT` | UInt16 | Parameter | ° | Cam Hút hộp OFF | Cam Box suction OFF |
| 12 |  | `CAM_CAP_HOP_ON_ACT` | UInt16 | Parameter | ° | Cam Cấp hộp ON | Cam Box feed ON |
| 13 |  | `CAM_CAP_HOP_OFF_ACT` | UInt16 | Parameter | ° | Cam Cấp hộp OFF | Cam Box feed OFF |
| 14 |  | `CAM_GAP_TAI_DUOI_ON_ACT` | UInt16 | Parameter | ° | Cam Gấp tai dưới ON | Cam Lower flap fold ON |
| 15 |  | `CAM_GAP_TAI_DUOI_OFF_ACT` | UInt16 | Parameter | ° | Cam Gấp tai dưới OFF | Cam Lower flap fold OFF |
| 16 |  | `CAM_IN_DATE_ON_ACT` | UInt16 | Parameter | ° | Cam In date ON | Cam Date printing ON |
| 17 |  | `CAM_IN_DATE_OFF_ACT` | UInt16 | Parameter | ° | Cam In date OFF | Cam Date printing OFF |
| 18 |  | `CAM_GAP_TAI_TREN_ON_ACT` | UInt16 | Parameter | ° | Cam Gấp tai trên ON | Cam Upper flap fold ON |
| 19 |  | `CAM_GAP_TAI_TREN_OFF_ACT` | UInt16 | Parameter | ° | Cam Gấp tai trên OFF | Cam Upper flap fold OFF |
| 20 |  | `CAM_CHAN_NAP_ON_ACT` | UInt16 | Parameter | ° | Cam Chặn nắp ON | Cam Lid stop ON |
| 21 |  | `CAM_CHAN_NAP_OFF_ACT` | UInt16 | Parameter | ° | Cam Chặn nắp OFF | Cam Lid stop OFF |
| 22 |  | `CAM_GAP_NAP_ON_ACT` | UInt16 | Parameter | ° | Cam Gấp nắp ON | Cam Lid fold ON |
| 23 |  | `CAM_GAP_NAP_OFF_ACT` | UInt16 | Parameter | ° | Cam Gấp nắp OFF | Cam Lid fold OFF |
| 24 |  | `CAM_DONG_NAP_ON_ACT` | UInt16 | Parameter | ° | Cam Đóng nắp ON | Cam Lid close ON |
| 25 |  | `CAM_DONG_NAP_OFF_ACT` | UInt16 | Parameter | ° | Cam Đóng nắp OFF | Cam Lid close OFF |
| 26 |  | `CAM_THANH_PHAM_ON_ACT` | UInt16 | Parameter | ° | Cam Thành phẩm ON | Cam Finished product ON |
| 27 |  | `CAM_THANH_PHAM_OFF_ACT` | UInt16 | Parameter | ° | Cam Thành phẩm OFF | Cam Finished product OFF |
| 28 |  | `CAM_HUT_TOA_ON_ACT` | UInt16 | Parameter | ° | Cam Hút toa ON | Cam Leaflet suction ON |
| 29 |  | `CAM_HUT_TOA_OFF_ACT` | UInt16 | Parameter | ° | Cam Hút toa OFF | Cam Leaflet suction OFF |
| 30 |  | `CAM_KEP_TOA_ON_ACT` | UInt16 | Parameter | ° | Cam Kẹp toa ON | Cam Leaflet clamp ON |
| 31 |  | `CAM_KEP_TOA_OFF_ACT` | UInt16 | Parameter | ° | Cam Kẹp toa OFF | Cam Leaflet clamp OFF |
| 32 |  | `CAM_KEO_TOA_ON_ACT` | UInt16 | Parameter | ° | Cam Kéo toa ON | Cam Leaflet pull ON |
| 33 |  | `CAM_KEO_TOA_OFF_ACT` | UInt16 | Parameter | ° | Cam Kéo toa OFF | Cam Leaflet pull OFF |
| 34 |  | `CAM_LAC_HUT_ON_ACT` | UInt16 | Parameter | ° | Cam Lắc hút ON | Cam Suction swing ON |
| 35 |  | `CAM_LAC_HUT_OFF_ACT` | UInt16 | Parameter | ° | Cam Lắc hút OFF | Cam Suction swing OFF |
| 36 |  | `CAM_CHOT_TOA_ON_ACT` | UInt16 | Parameter | ° | Cam Chọt toa ON | Cam Leaflet push ON |
| 37 |  | `CAM_CHOT_TOA_OFF_ACT` | UInt16 | Parameter | ° | Cam Chọt toa OFF | Cam Leaflet push OFF |
| 38 |  | `CAM_CAP_TOA_HD_ON_ACT` | UInt16 | Parameter | ° | Cam Cấp toa hoạt động ON | Cam Leaflet feed active ON |
| 39 |  | `CAM_CAP_TOA_HD_OFF_ACT` | UInt16 | Parameter | ° | Cam Cấp toa hoạt động OFF | Cam Leaflet feed active OFF |

## PC → PLC (PC writes, PLC reads)

| Address | Bit | Tag | Type | Category | Unit / range | Meaning (vi) | Meaning (en) |
|---|---|---|---|---|---|---|---|
| 100 |  | `PC_HEARTBEAT` | UInt16 | Internal |  | Nhịp PC | PC heartbeat |
| 101 |  | `LOGIN_LEVEL` | UInt16 | Internal |  | Cấp đăng nhập | Login level |
| 102 |  | `BOX_CAM_SCANNING` | UInt16 | Internal |  | Camera Hộp đang quét | Box camera scanning |
| 103 |  | `LEAFLET_CAM_SCANNING` | UInt16 | Internal |  | Camera Toa đang quét | Leaflet camera scanning |
| 104 |  | `SPEED_SETPOINT` | UInt16 | Internal | hộp/phút 0…300 | Tốc độ cài đặt | Speed setpoint |
| 105 |  | `TEMP_SETPOINT` | Int16 | Internal | °C 0…250 | Nhiệt độ cài đặt | Temperature setpoint |
| 110 |  | `CAM_HUT_HOP_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Hút hộp ON | Cam Box suction ON |
| 111 |  | `CAM_HUT_HOP_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Hút hộp OFF | Cam Box suction OFF |
| 112 |  | `CAM_CAP_HOP_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Cấp hộp ON | Cam Box feed ON |
| 113 |  | `CAM_CAP_HOP_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Cấp hộp OFF | Cam Box feed OFF |
| 114 |  | `CAM_GAP_TAI_DUOI_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp tai dưới ON | Cam Lower flap fold ON |
| 115 |  | `CAM_GAP_TAI_DUOI_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp tai dưới OFF | Cam Lower flap fold OFF |
| 116 |  | `CAM_IN_DATE_ON_SP` | UInt16 | Internal | ° 0…359 | Cam In date ON | Cam Date printing ON |
| 117 |  | `CAM_IN_DATE_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam In date OFF | Cam Date printing OFF |
| 118 |  | `CAM_GAP_TAI_TREN_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp tai trên ON | Cam Upper flap fold ON |
| 119 |  | `CAM_GAP_TAI_TREN_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp tai trên OFF | Cam Upper flap fold OFF |
| 120 |  | `CAM_CHAN_NAP_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Chặn nắp ON | Cam Lid stop ON |
| 121 |  | `CAM_CHAN_NAP_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Chặn nắp OFF | Cam Lid stop OFF |
| 122 |  | `CAM_GAP_NAP_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp nắp ON | Cam Lid fold ON |
| 123 |  | `CAM_GAP_NAP_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Gấp nắp OFF | Cam Lid fold OFF |
| 124 |  | `CAM_DONG_NAP_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Đóng nắp ON | Cam Lid close ON |
| 125 |  | `CAM_DONG_NAP_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Đóng nắp OFF | Cam Lid close OFF |
| 126 |  | `CAM_THANH_PHAM_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Thành phẩm ON | Cam Finished product ON |
| 127 |  | `CAM_THANH_PHAM_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Thành phẩm OFF | Cam Finished product OFF |
| 128 |  | `CAM_HUT_TOA_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Hút toa ON | Cam Leaflet suction ON |
| 129 |  | `CAM_HUT_TOA_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Hút toa OFF | Cam Leaflet suction OFF |
| 130 |  | `CAM_KEP_TOA_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Kẹp toa ON | Cam Leaflet clamp ON |
| 131 |  | `CAM_KEP_TOA_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Kẹp toa OFF | Cam Leaflet clamp OFF |
| 132 |  | `CAM_KEO_TOA_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Kéo toa ON | Cam Leaflet pull ON |
| 133 |  | `CAM_KEO_TOA_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Kéo toa OFF | Cam Leaflet pull OFF |
| 134 |  | `CAM_LAC_HUT_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Lắc hút ON | Cam Suction swing ON |
| 135 |  | `CAM_LAC_HUT_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Lắc hút OFF | Cam Suction swing OFF |
| 136 |  | `CAM_CHOT_TOA_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Chọt toa ON | Cam Leaflet push ON |
| 137 |  | `CAM_CHOT_TOA_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Chọt toa OFF | Cam Leaflet push OFF |
| 138 |  | `CAM_CAP_TOA_HD_ON_SP` | UInt16 | Internal | ° 0…359 | Cam Cấp toa hoạt động ON | Cam Leaflet feed active ON |
| 139 |  | `CAM_CAP_TOA_HD_OFF_SP` | UInt16 | Internal | ° 0…359 | Cam Cấp toa hoạt động OFF | Cam Leaflet feed active OFF |

## Adding a signal

1. PLC programmer adds the register / bit.
2. Add one entry to `config/tags.json` (category Alarm / Parameter / State / Process).
3. Restart the application: the new tag is polled, alarms appear in the alarm list, parameters are audited. The SHA-256 of tags.json is recorded in the audit trail at start-up (change control).
4. Re-run `build/tools/make_plc_doc.py` to refresh this document.
