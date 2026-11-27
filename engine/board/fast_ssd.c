/*
 * TOPST Toolkit - fast post-processing for SSD-type detectors on the board (Cortex-A53, NEON).
 *
 * The app compiles this file into net.so in place of the compiler's post_process.c. That generated file is
 * included below, unchanged, so the toolkit's own code stays in the library as run_post_process_reference():
 * it is the fallback for every network this file does not handle, and what this file is compared with.
 *
 * What is different from the toolkit code
 *   The toolkit code copies the whole score tensor and turns every value of every box into a float through a
 *   table (mb2-ssd-lite: 3,000 boxes x 21 classes), with a softmax per box. Almost every box is background.
 *   This file first looks at the raw int8 values of a box, sixteen at a time: its largest class value against
 *   what that value would have to be for the box to count. Only the few boxes that may count get the float maths.
 *     softmax with a background class : a class can reach the threshold only if exp(class) is large enough next
 *                                       to exp(background); the smallest such class value is tabulated for each
 *                                       background value (a slightly loose bound, so no box is ever left out)
 *     sigmoid, from a table or by the NPU : a value passes exactly when it is at or above one int8 value
 *
 * What is the same
 *   The boxes that count, their order, every float operation on them and the NMS (the toolkit's own nms_float /
 *   fastnms_float are called). The detections are the same values in the same order; compared on a TOPST AI-G
 *   board (docs/design.md section 5.8).
 *
 * On the board (an environment variable, read once when the first frame is processed)
 *   TOPST_POST_PROCESS=reference   run the toolkit code instead (to compare times or results without a rebuild)
 *   TOPST_POST_PROCESS=quiet       do not print the detections of every frame
 *
 * Build: one extra gcc call next to the Makefile's, see NetSoBuild in the app.
 *        -O3 -mcpu=cortex-a53 -ffp-contract=off (multiply-adds are fused only where written as fmaf below)
 */
#define run_post_process run_post_process_reference
#include "post_process.c"
#undef run_post_process

#include <arm_neon.h>
#include <stdlib.h>
#include <string.h>

#include "detector/detector.h"

#define FS_MAX_CLASS 1024               /* MAX_CLASS_NUM of detector/detector.c */
#define FS_INLINE static inline __attribute__((always_inline))

int run_post_process(void *net_inst, void *output_base, int num_input, enlight_objs_t *detected_objects);

/* A box that may hold an object: its index, its scores as the toolkit code leaves them (one float per class,
 * background first when there is one) and its decoded rectangle. */
typedef struct {
    int box;
    int has_rect;
    box_f_t rect;
} fs_cand_t;

/* ---- the rectangle of a box: build_box_info in detector/ssd_detector.c, operation for operation.
 * "x * anchor_w + anchor_x" is one fused multiply-add in the toolkit build. */
static void fs_rect(box_f_t *box, const int8_t *xywh, const float *prior, float scl, const float *exp_tbl)
{
    float x, y, w, h;
    float anchor_x, anchor_y, anchor_w, anchor_h;

    x = xywh[0] / scl;
    y = xywh[1] / scl;
    w = exp_tbl[xywh[2]];
    h = exp_tbl[xywh[3]];

    anchor_x = prior[0];
    anchor_y = prior[1];
    anchor_w = prior[2];
    anchor_h = prior[3];

    x = __builtin_fmaf(x, anchor_w, anchor_x);
    y = __builtin_fmaf(y, anchor_h, anchor_y);
    w = w * anchor_w;
    h = h * anchor_h;

    box->x_min = x - w/2;
    box->x_max = x + w/2;
    box->y_min = y - h/2;
    box->y_max = y + h/2;
    box->area  = w * h;
}

/* The largest class value of one box, the background value and the padding left out. */
FS_INLINE int fs_max_class(const int8_t *q, int blocks, uint8x16_t first_mask, uint8x16_t last_mask)
{
    const int8x16_t lowest = vdupq_n_s8(-128);
    int8x16_t m;
    int i;
    if (blocks == 1)
        return vmaxvq_s8(vbslq_s8(vandq_u8(first_mask, last_mask), vld1q_s8(q), lowest));
    m = vbslq_s8(first_mask, vld1q_s8(q), lowest);
    for (i = 1; i < blocks - 1; i++)
        m = vmaxq_s8(m, vld1q_s8(q + 16 * i));
    m = vmaxq_s8(m, vbslq_s8(last_mask, vld1q_s8(q + 16 * (blocks - 1)), lowest));
    return vmaxvq_s8(m);
}

/* Returns 0 when it produced the detections (in the detector's own buffer, like the toolkit code),
 * -1 when this network or this frame is left to the toolkit code. Nothing is printed before it returns. */
static int fs_detect(enlight_network_t *inst, void *output_base, int *found)
{
    enlight_ssd_detector_t *p = (enlight_ssd_detector_t *)inst->post_proc_extension;
    enlight_act_tensor_t *loc = p->loc_tensors[0], *conf = p->conf_tensors[0];
    const int bg = p->bg_in_cls;
    const int classes = p->num_class + bg;              /* values per box, background included */
    const int num_box = p->num_box;
    const int stride = (classes + 31) & ~31;            /* bytes per box in the score tensor */
    const float th_conf = p->th_conf, th_iou = p->th_iou;
    const int softmax = p->logistic == LOGISTIC_SOFTMAX;
    const int fast_nms = p->nms_method == NMS_FAST;
    const float *score_tbl = &p->conf_logistic_tables[128];     /* exp for softmax, sigmoid for LOGISTIC_SIGMOID */
    const float *exp_tbl = &p->loc_exp_tables[128];
    int8_t need[256];           /* softmax: for a background value, the smallest class value that may pass */
    int q_pass = 128;           /* sigmoid: the smallest value that passes */
    int i, c, q;
    int status = -1;

    fs_cand_t *cand = 0;
    float *score = 0;           /* [candidate][class] */
    int num_cand = 0, cap = 0;
    box_f_t *nms_box = 0;
    float *nms_score = 0;
    uint16_t *nms_cls = 0;
    detection_f_t *detection = 0;

    /* ---- only the layouts that were compared with the toolkit code on a board */
    if (p->max_detection > 0 || p->loc_tensors_num < 1 || p->conf_tensors_num < 1)
        return -1;
    if (p->nms_method != NMS_DEFAULT && p->nms_method != NMS_FAST)
        return -1;
    if (p->logistic != LOGISTIC_SOFTMAX && p->logistic != LOGISTIC_SIGMOID && p->logistic != LOGISTIC_NONE)
        return -1;
    if (bg != 0 && bg != 1)
        return -1;
    if (softmax && !bg)
        return -1;                                      /* no background to compare with: nothing to gain */
    if (num_box < 1 || classes - bg < 1 || classes > FS_MAX_CLASS)
        return -1;
    if (loc == 0 || conf == 0 || loc->output == 0 || conf->output == 0
        || loc->data_type != ENLIGHT_DTYPE_INT8 || conf->data_type != ENLIGHT_DTYPE_INT8)
        return -1;
    const int loc_step = ((loc->dimensions[1] + 31) & 0xFFE0) * loc->dimensions[3];   /* bytes from box to box */
    if (loc->dimensions[1] < 4 || loc_step < 4 || (long long)loc_step * (num_box - 1) + 4 > loc->size
        || (long long)stride * num_box > conf->size)
        return -1;

    for (i = 0; i < p->loc_tensors_num && i < MAX_NUM_LOC; i++)
        p->loc_tensors[i]->base = (char *)output_base;
    for (i = 0; i < p->conf_tensors_num && i < MAX_NUM_CONF; i++)
        p->conf_tensors[i]->base = (char *)output_base;
    const int8_t *conf_q = (const int8_t *)&conf->base[conf->buf];
    const int8_t *loc_q = (const int8_t *)&loc->base[loc->buf];
    const float loc_scl = loc->scale;

    /* ---- what a box needs to be looked at closely */
    if (p->logistic != LOGISTIC_NONE)
        for (q = -127; q < 128; q++)
            if (score_tbl[q] < score_tbl[q - 1])
                return -1;                              /* the bounds below need a table that never decreases */
    if (softmax) {
        /* A class passes when e >= sum * th, and sum holds at least e and the background's e_bg (every entry of
         * an exp table is positive). So it can pass only if e >= th * (e + e_bg). The slack covers the rounding
         * of the toolkit's float sum over all classes, so that no box that counts is ever left out; a box let
         * through for nothing is sorted out by the exact maths below. */
        const double slack = (classes + 4) * 1.2e-7;
        int t = -128;
        if (!(score_tbl[-128] >= 0))
            return -1;
        /* The table never decreases, so the class value needed never decreases with the background value:
         * one sweep over both. At worst only the largest value (127) is looked at closely. */
        for (q = -128; q < 128; q++) {
            const double e_bg = score_tbl[q];
            while (t < 127 && !(score_tbl[t] * (1.0 + slack) >= (double)th_conf * ((double)score_tbl[t] + e_bg) * (1.0 - slack)))
                t++;
            need[q + 128] = (int8_t)t;
        }
    } else {
        for (q = -128; q < 128; q++) {
            const float v = p->logistic == LOGISTIC_NONE ? (float)(int8_t)q / ENLIGHT_NPU_SIG_OUT_SCL : score_tbl[q];
            if (v >= th_conf) {
                q_pass = q;
                break;
            }
        }
        if (q_pass > 127) {             /* no value passes: no detections */
            p->detected_objects.cnt = 0;
            *found = 0;
            return 0;
        }
    }

    /* ---- pass 1: the boxes that count, with their scores as the toolkit code computes them */
    {
        const int blocks = (classes + 15) / 16;
        uint8_t bytes[16];
        for (i = 0; i < 16; i++)
            bytes[i] = (i == 0 && bg) ? 0 : 0xFF;
        const uint8x16_t first_mask = vld1q_u8(bytes);
        for (i = 0; i < 16; i++)
            bytes[i] = (16 * (blocks - 1) + i) < classes ? 0xFF : 0;
        const uint8x16_t last_mask = vld1q_u8(bytes);

        for (i = 0; i < num_box; i++) {
            const int8_t *v = conf_q + (size_t)stride * i;
            const int top = fs_max_class(v, blocks, first_mask, last_mask);
            int valid = 0;
            float *row;

            if (softmax ? top < need[v[0] + 128] : top < q_pass)
                continue;

            if (num_cand == cap) {
                const int grown = cap ? cap * 2 : 64;
                fs_cand_t *bigger = (fs_cand_t *)realloc(cand, sizeof(fs_cand_t) * grown);
                if (!bigger) goto done;
                cand = bigger;
                float *more = (float *)realloc(score, sizeof(float) * (size_t)grown * classes);
                if (!more) goto done;
                score = more;
                cap = grown;
            }
            row = score + (size_t)num_cand * classes;

            if (softmax) {
                /* softmax_with_threshold_float */
                float e_array[FS_MAX_CLASS], sum = 0, sum_x_th;
                for (c = 0; c < classes; c++) {
                    const float e = score_tbl[v[c]];
                    e_array[c] = e;
                    sum += e;
                }
                if (!(sum > 0))
                    continue;
                sum_x_th = sum * th_conf;
                for (c = bg; c < classes; c++) {
                    if (e_array[c] >= sum_x_th) {
                        if (!valid) {
                            memset(row, 0, sizeof(float) * classes);
                            valid = 1;
                        }
                        row[c] = e_array[c] / sum;
                    }
                }
            } else {
                /* sigmoid_with_threshold_float; the background slot is never read afterwards */
                row[0] = 0;
                for (c = bg; c < classes; c++) {
                    float sig_value = (float)v[c] / ENLIGHT_NPU_SIG_OUT_SCL;
                    if (p->logistic != LOGISTIC_NONE)
                        sig_value = score_tbl[v[c]];
                    if (sig_value >= th_conf)
                        valid = 1;
                    row[c] = sig_value;
                }
            }
            if (!valid)
                continue;
            cand[num_cand].box = i;
            cand[num_cand].has_rect = 0;
            num_cand++;
        }
    }

    /* ---- pass 2: thresholding and NMS, with the toolkit's own NMS functions and in its order */
    {
        int entries = 0, n;
        enlight_objs_t *objs = &p->detected_objects;
        const int *img = inst->img_sizes;

        if (fast_nms) {
            entries = num_cand;
        } else {
            for (i = 0; i < num_cand; i++)
                for (c = bg; c < classes; c++)
                    if (score[(size_t)i * classes + c] >= th_conf)
                        entries++;
        }
        detection = alloc_detection_float(entries > 0 ? entries : 1);
        nms_box = (box_f_t *)malloc(sizeof(box_f_t) * (num_cand > 0 ? num_cand : 1));
        nms_score = (float *)malloc(sizeof(float) * (num_cand > 0 ? num_cand : 1));
        nms_cls = (uint16_t *)malloc(sizeof(uint16_t) * (num_cand > 0 ? num_cand : 1));
        if (!detection || !nms_box || !nms_score || !nms_cls)
            goto done;

#define FS_RECT(k) do { \
            if (!cand[k].has_rect) { \
                fs_rect(&cand[k].rect, loc_q + (size_t)loc_step * cand[k].box, p->default_box + 4 * cand[k].box, loc_scl, exp_tbl); \
                cand[k].has_rect = 1; \
            } } while (0)

        if (fast_nms) {
            /* thresholding_by_default_box: the best class of the box, the first one on a tie; the class number
             * does not count the background here */
            cand_f_t list;
            n = 0;
            for (i = 0; i < num_cand; i++) {
                const float *s = score + (size_t)i * classes + bg;
                int max_idx = 0;
                float max_val = s[0];
                for (c = 1; c < classes - bg; c++) {
                    if (max_val < s[c]) {
                        max_val = s[c];
                        max_idx = c;
                    }
                }
                if (max_val >= th_conf) {
                    FS_RECT(i);
                    nms_box[n] = cand[i].rect;
                    nms_cls[n] = (uint16_t)max_idx;
                    nms_score[n] = max_val;
                    n++;
                }
            }
            list.box = nms_box;
            list.score = nms_score;
            list.cls = nms_cls;
            list.n = n;
            if (n > 0)
                fastnms_float(detection, &list, th_iou);
        } else {
            /* thresholding_with_candidate, class by class */
            for (c = bg; c < classes; c++) {
                cand_f_t list;
                n = 0;
                for (i = 0; i < num_cand; i++) {
                    const float s = score[(size_t)i * classes + c];
                    if (s >= th_conf) {
                        FS_RECT(i);
                        nms_box[n] = cand[i].rect;
                        nms_score[n] = s;
                        n++;
                    }
                }
                if (n == 0)
                    continue;
                list.box = nms_box;
                list.score = nms_score;
                list.cls = nms_cls;
                list.n = n;
                nms_float(detection, &list, c, th_iou);
            }
        }
#undef FS_RECT

        n = detection->n;
        *found = n;
        if (n > MAX_DETECT_BOX)
            n = MAX_DETECT_BOX;
        objs->cnt = n;
        for (i = 0; i < n; i++) {
            const box_f_t *b = detection->box + i;
            enlight_obj_t *o = &objs->obj[i];
            o->x_min = img[2] * b->x_min;
            o->x_max = img[2] * b->x_max;
            o->y_min = img[1] * b->y_min;
            o->y_max = img[1] * b->y_max;
            o->score = detection->score[i];
            o->cls = detection->cls[i];
            o->img_w = img[2];
            o->img_h = img[1];
        }
        status = 0;
    }

done:
    if (detection)
        free_detection_float(detection);
    free(nms_box);
    free(nms_score);
    free(nms_cls);
    free(cand);
    free(score);
    return status;
}

/* The entry point the board library calls (network.c: run_post). Same arguments, same log lines and the same
 * result as the toolkit's generated function, which is kept as run_post_process_reference(). */
int run_post_process(void *net_inst, void *output_base, int num_input, enlight_objs_t *detected_objects)
{
    static int mode = -1;               /* 0 = fast, 1 = fast without the log lines, 2 = the toolkit code */
    enlight_network_t *inst = (enlight_network_t *)net_inst;
    enlight_objs_t *objs;
    int i, found = 0;

    if (mode < 0) {
        const char *choice = getenv("TOPST_POST_PROCESS");
        mode = choice == 0 ? 0 : strcmp(choice, "reference") == 0 ? 2 : strcmp(choice, "quiet") == 0 ? 1 : 0;
    }
    if (mode == 2 || fs_detect(inst, output_base, &found) != 0)
        return run_post_process_reference(net_inst, output_base, num_input, detected_objects);

    objs = &((enlight_ssd_detector_t *)inst->post_proc_extension)->detected_objects;

    if (mode == 0) {                    /* the lines the toolkit code prints, in its order */
        enlight_detector_log("%d object is detected\n", found);
        if (found > MAX_DETECT_BOX)
            enlight_detector_log("detection result %d is limited to MAX VALUE: %d\n", found, MAX_DETECT_BOX);
        for (i = 0; i < objs->cnt; i++) {
            enlight_obj_t *obj = &objs->obj[i];
            post_process_log(
                "x1 y1 x2 y2 = %8.4f %8.4f %8.4f %8.4f class: %4d score: %8.4f\n",
                obj->x_min, obj->y_min, obj->x_max, obj->y_max, obj->cls, obj->score * 100.);
        }
    }

    if (detected_objects != 0) {
        detected_objects->cnt = objs->cnt;
        for (i = 0; i < objs->cnt; i++) {
            enlight_obj_t *obj = &objs->obj[i];
            enlight_obj_t *ret_obj = &detected_objects->obj[i];

            ret_obj->x_min = obj->x_min;
            ret_obj->x_max = obj->x_max;
            ret_obj->y_min = obj->y_min;
            ret_obj->y_max = obj->y_max;
            ret_obj->cls   = obj->cls;
            ret_obj->score = obj->score;
            ret_obj->img_w = obj->img_w;
            ret_obj->img_h = obj->img_h;
        }
    }
    return num_input;
}
