/*
 * AutoSwitch KVM host channel for ZMK.
 *
 * Lets the AutoSwitch KVM app on the connected computer read and change this keyboard's
 * Bluetooth profile, so the keyboard follows a KVM switch: when the KVM leaves a computer, that
 * computer's AutoSwitch tells the keyboard to select the other computer's profile. This is the
 * ZMK counterpart of Logitech's HID++ "Change Host" feature (AutoSwitch SPECIFICATION.md §5a,
 * type 5 "Switches channel on request").
 *
 * Transport: zzeneg/zmk-raw-hid (vendor HID collection, usage page 0xFF60 / usage 0x61 by default,
 * CONFIG_RAW_HID_REPORT_SIZE-byte reports, over USB and Bluetooth). Protocol v1, every report:
 *
 *   byte 0..1  'A' 'S'          magic (ignores other raw HID traffic, e.g. qmk-hid-host)
 *   byte 2     1                protocol version
 *   byte 3     command          0x01 GET_INFO, 0x02 SELECT, 0x03 SET_MARK; replies: command | 0x80
 *   byte 4..   arguments / reply payload
 *
 *   GET_INFO          -> status, profile count, active profile, paired bitmask (lo, hi),
 *                        then one mark byte per profile (0 = none)
 *   SELECT  profile   -> status; the profile is selected CONFIG_ZMK_AUTOSWITCH_SELECT_DELAY_MS
 *                        later, so the reply leaves before the connection to this host drops
 *   SET_MARK profile, value -> status; stored in settings together with the profile's bonded
 *                        address, and reported as 0 once that profile is bonded to another host
 *
 *   status: 0 OK, 1 invalid argument, 2 unknown command / unsupported version
 */

#include <string.h>

#include <zephyr/bluetooth/addr.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>
#include <zephyr/settings/settings.h>
#include <zephyr/sys/util.h>

#include <zmk/ble.h>
#include <zmk/event_manager.h>

#include <raw_hid/events.h>

LOG_MODULE_REGISTER(zmk_autoswitch, CONFIG_ZMK_LOG_LEVEL);

#define AS_MAGIC_0 'A'
#define AS_MAGIC_1 'S'
#define AS_VERSION 1

#define AS_CMD_GET_INFO 0x01
#define AS_CMD_SELECT 0x02
#define AS_CMD_SET_MARK 0x03
#define AS_REPLY_FLAG 0x80

#define AS_STATUS_OK 0
#define AS_STATUS_INVALID_ARG 1
#define AS_STATUS_UNSUPPORTED 2

#define AS_HEADER_LEN 4
#define AS_INFO_FIXED_LEN 5 /* status, count, active, paired lo, paired hi */
#define AS_MAX_PROFILES 16  /* paired bitmask is 16 bits */

#define AS_PROFILES MIN(ZMK_BLE_PROFILE_COUNT, AS_MAX_PROFILES)

BUILD_ASSERT(CONFIG_RAW_HID_REPORT_SIZE >= AS_HEADER_LEN + AS_INFO_FIXED_LEN + AS_PROFILES,
             "CONFIG_RAW_HID_REPORT_SIZE is too small for the AutoSwitch GET_INFO reply");

/* A mark remembers which host wrote it: it only counts while the profile is bonded to that host. */
struct as_mark {
    bt_addr_le_t addr;
    uint8_t value;
};

static struct as_mark marks[AS_PROFILES];

static uint8_t reply[CONFIG_RAW_HID_REPORT_SIZE];

/* ---- Deferred work: profile selection and settings writes stay out of the event path ---- */

static uint8_t pending_profile;

static void select_work_handler(struct k_work *work) {
    LOG_INF("AutoSwitch: selecting BLE profile %d", pending_profile);
    int err = zmk_ble_prof_select(pending_profile);
    if (err < 0) {
        LOG_ERR("AutoSwitch: selecting profile %d failed: %d", pending_profile, err);
    }
}

static K_WORK_DELAYABLE_DEFINE(select_work, select_work_handler);

static void save_work_handler(struct k_work *work) {
    int err = settings_save_one("autoswitch/marks", marks, sizeof(marks));
    if (err < 0) {
        LOG_ERR("AutoSwitch: saving marks failed: %d", err);
    }
}

static K_WORK_DEFINE(save_work, save_work_handler);

/* ---- Settings ---- */

static int as_settings_set(const char *name, size_t len, settings_read_cb read_cb, void *cb_arg) {
    const char *next;
    if (settings_name_steq(name, "marks", &next) && !next) {
        if (len != sizeof(marks)) {
            /* Profile count changed since they were saved: start over. */
            LOG_WRN("AutoSwitch: ignoring saved marks (%d bytes, expected %d)", len, sizeof(marks));
            return 0;
        }
        int rc = read_cb(cb_arg, marks, sizeof(marks));
        return rc < 0 ? rc : 0;
    }
    return -ENOENT;
}

SETTINGS_STATIC_HANDLER_DEFINE(zmk_autoswitch, "autoswitch", NULL, as_settings_set, NULL, NULL);

/* ---- Protocol ---- */

static uint8_t mark_for(uint8_t profile) {
    if (profile >= AS_PROFILES || zmk_ble_profile_is_open(profile)) {
        return 0;
    }
    const bt_addr_le_t *bonded = zmk_ble_profile_address(profile);
    if (bonded == NULL || bt_addr_le_cmp(bonded, &marks[profile].addr) != 0) {
        return 0; /* bonded to another host since the mark was written */
    }
    return marks[profile].value;
}

static void send_reply(uint8_t command, uint8_t status, const uint8_t *payload, size_t len) {
    memset(reply, 0, sizeof(reply));
    reply[0] = AS_MAGIC_0;
    reply[1] = AS_MAGIC_1;
    reply[2] = AS_VERSION;
    reply[3] = command | AS_REPLY_FLAG;
    reply[4] = status;
    if (payload != NULL && len > 0) {
        memcpy(&reply[5], payload, MIN(len, sizeof(reply) - 5));
    }
    raise_raw_hid_sent_event((struct raw_hid_sent_event){.data = reply, .length = sizeof(reply)});
}

static void handle_get_info(void) {
    uint8_t payload[AS_INFO_FIXED_LEN - 1 + AS_PROFILES] = {0};
    int active = zmk_ble_active_profile_index();
    uint16_t paired = 0;
    for (uint8_t i = 0; i < AS_PROFILES; i++) {
        if (!zmk_ble_profile_is_open(i)) {
            paired |= BIT(i);
        }
        payload[4 + i] = mark_for(i);
    }
    payload[0] = AS_PROFILES;
    payload[1] = active < 0 ? 0xFF : (uint8_t)active;
    payload[2] = paired & 0xFF;
    payload[3] = paired >> 8;
    send_reply(AS_CMD_GET_INFO, AS_STATUS_OK, payload, sizeof(payload));
}

static void handle_select(uint8_t profile) {
    if (profile >= AS_PROFILES) {
        send_reply(AS_CMD_SELECT, AS_STATUS_INVALID_ARG, NULL, 0);
        return;
    }
    send_reply(AS_CMD_SELECT, AS_STATUS_OK, NULL, 0);
    pending_profile = profile;
    k_work_reschedule(&select_work, K_MSEC(CONFIG_ZMK_AUTOSWITCH_SELECT_DELAY_MS));
}

static void handle_set_mark(uint8_t profile, uint8_t value) {
    if (profile >= AS_PROFILES || zmk_ble_profile_is_open(profile)) {
        send_reply(AS_CMD_SET_MARK, AS_STATUS_INVALID_ARG, NULL, 0);
        return;
    }
    const bt_addr_le_t *bonded = zmk_ble_profile_address(profile);
    if (bonded == NULL) {
        send_reply(AS_CMD_SET_MARK, AS_STATUS_INVALID_ARG, NULL, 0);
        return;
    }
    bool changed = marks[profile].value != value || bt_addr_le_cmp(bonded, &marks[profile].addr) != 0;
    bt_addr_le_copy(&marks[profile].addr, bonded);
    marks[profile].value = value;
    send_reply(AS_CMD_SET_MARK, AS_STATUS_OK, NULL, 0);
    if (changed) {
        k_work_submit(&save_work);
    }
}

/* Requests are handled on the system work queue: the raw HID event is raised from the Bluetooth
 * RX thread (a GATT write), and the reply is a GATT notification, which shouldn't be sent from
 * there. The host waits for each reply before sending the next request, so one slot is enough. */
static uint8_t request[CONFIG_RAW_HID_REPORT_SIZE];
static size_t request_len;

static void request_work_handler(struct k_work *work) {
    const uint8_t *data = request;
    if (data[2] != AS_VERSION) {
        send_reply(data[3], AS_STATUS_UNSUPPORTED, NULL, 0);
        return;
    }
    uint8_t arg0 = request_len > 4 ? data[4] : 0;
    uint8_t arg1 = request_len > 5 ? data[5] : 0;
    switch (data[3]) {
    case AS_CMD_GET_INFO:
        handle_get_info();
        break;
    case AS_CMD_SELECT:
        handle_select(arg0);
        break;
    case AS_CMD_SET_MARK:
        handle_set_mark(arg0, arg1);
        break;
    default:
        send_reply(data[3], AS_STATUS_UNSUPPORTED, NULL, 0);
        break;
    }
}

static K_WORK_DEFINE(request_work, request_work_handler);

static int raw_hid_received_listener(const zmk_event_t *eh) {
    struct raw_hid_received_event *event = as_raw_hid_received_event(eh);
    if (event == NULL || event->length < AS_HEADER_LEN) {
        return ZMK_EV_EVENT_BUBBLE;
    }
    if (event->data[0] != AS_MAGIC_0 || event->data[1] != AS_MAGIC_1) {
        return ZMK_EV_EVENT_BUBBLE; /* not ours */
    }
    memset(request, 0, sizeof(request));
    request_len = MIN(event->length, sizeof(request));
    memcpy(request, event->data, request_len);
    k_work_submit(&request_work);
    return ZMK_EV_EVENT_HANDLED;
}

ZMK_LISTENER(zmk_autoswitch, raw_hid_received_listener);
ZMK_SUBSCRIPTION(zmk_autoswitch, raw_hid_received_event);
