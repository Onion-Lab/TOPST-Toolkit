/*
 * TOPST Toolkit - fast post-processing for YOLO-type detectors on the board (Cortex-A53, NEON).
 *
 * The app compiles this file into net.so in place of the compiler's post_process.c. That generated file is
 * included below, unchanged, so the toolkit's own code stays in the library as run_post_process_reference():
 * it is the fallback for every network this file does not handle, and what this file is compared with.
 *
 * What is different from the toolkit code
 *   The toolkit code looks at every value of every grid cell through a 256-entry float table to find the cells
 *   worth decoding (yolov8m: 8,400 cells x 80 classes). An int8 value passes the threshold exactly when it is at
 *   or above one particular int8 value, so this file compares the raw bytes instead, sixteen at a time, and
 *   touches the tables only for the few cells that pass.
 *
 * What is the same
 *   The cells that pass, the order they are visited in, every float operation on them and the NMS (the toolkit's
 *   own nms_float / fastnms_float are called). The detections are the same values in the same order; this was
 *   compared on a TOPST AI-G board for YOLOv3/v4/v5/v6/v7/v8/X networks (docs/design.md section 5.8).
 *
 * On the board (an environment variable, read once when the first frame is processed)
 *   TOPST_POST_PROCESS=reference   run the toolkit code instead (to compare times or results without a rebuild)
 *   TOPST_POST_PROCESS=quiet       do not print the detections of every frame. The toolkit code prints one line
 *                                  per detection, and on this CPU printing three lines costs about as much as
 *                                  a third of the whole post-processing.
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

#define FY_MAX_REG 64
#define FY_PREFETCH_AHEAD 2048          /* bytes ahead of the cell being read (objectness scan) */

int run_post_process(void *net_inst, void *output_base, int num_input, enlight_objs_t *detected_objects);

/* One output scale: where its values are and which tables turn them into numbers.
 * Tables are indexed by the int8 value itself (-128..127), as in the toolkit code. */
typedef struct {
    enlight_act_tensor_t *loc, *conf, *obj;
    int grid[2];                        /* rows, columns */
    const float *prior;                 /* default boxes: len_pri floats per (cell, anchor) */
    const float *conf_sig, *loc_sig, *loc_exp, *obj_sig;
    float loc_scale;
    int q_pass;                         /* smallest int8 value whose table entry reaches th_conf; 128 = none */
} fy_scale_t;

/* Candidates that passed the threshold, in the order the toolkit code produces them. */
typedef struct {
    box_f_t *box;
    float *score;
    uint16_t *cls;
    int n, cap;
} fy_list_t;

static int fy_reserve(fy_list_t *l, int more)
{
    if (l->n + more <= l->cap)
        return 0;
    int cap = l->cap ? l->cap : 256;
    while (cap < l->n + more)
        cap *= 2;
    box_f_t *box = (box_f_t *)realloc(l->box, sizeof(box_f_t) * cap);
    if (!box) return -1;
    l->box = box;
    float *score = (float *)realloc(l->score, sizeof(float) * cap);
    if (!score) return -1;
    l->score = score;
    uint16_t *cls = (uint16_t *)realloc(l->cls, sizeof(uint16_t) * cap);
    if (!cls) return -1;
    l->cls = cls;
    l->cap = cap;
    return 0;
}

static void fy_release(fy_list_t *l)
{
    free(l->box);
    free(l->score);
    free(l->cls);
    memset(l, 0, sizeof *l);
}

/* The smallest int8 value whose table entry is >= th (128 when there is none).
 * Only valid for a table that never decreases; *rising is cleared otherwise. */
static int fy_first_passing(const float *tbl, float th, int *rising)
{
    int q, first = 128;
    for (q = -128; q < 128; q++) {
        if (q > -128 && tbl[q] < tbl[q - 1])
            *rising = 0;
        if (first == 128 && tbl[q] >= th)
            first = q;
    }
    return first;
}

static int fy_tensor_ok(const enlight_act_tensor_t *t, int channels, const int *grid)
{
    return t != 0 && t->output != 0 && t->data_type == ENLIGHT_DTYPE_INT8
        && t->dimensions[1] >= channels && t->dimensions[2] >= grid[0] && t->dimensions[3] >= grid[1];
}

/* The bytes of one grid cell: channel c of cell (y, x) is at [c] (enlight_get_tensor_qdata_by_off, output != 0). */
static inline const int8_t *fy_cell(const enlight_act_tensor_t *t, int y, int x)
{
    const int c = (t->dimensions[1] + 31) & 0xFFE0;
    return (const int8_t *)&t->base[t->buf] + (c * x) + (c * t->dimensions[3] * y);
}

/* ---- boxes: the arithmetic of build_box_info* in detector/yolo_detector.c, operation for operation */

static inline void fy_box_v3(box_f_t *box, const int8_t *loc, const float *prior, const fy_scale_t *s)
{
    float x, y, w, h, anchor_x, anchor_y, anchor_w, anchor_h;

    x = s->loc_sig[loc[0]];
    y = s->loc_sig[loc[1]];
    x /= s->grid[1];
    y /= s->grid[0];

    anchor_x = prior[0];
    anchor_y = prior[1];
    anchor_w = prior[2];
    anchor_h = prior[3];

    x = x + anchor_x;
    y = y + anchor_y;

    w = s->loc_exp[loc[2]] * anchor_w;
    h = s->loc_exp[loc[3]] * anchor_h;

    w /= 2.;
    h /= 2.;

    box->x_min = x - w;
    box->x_max = x + w;
    box->y_min = y - h;
    box->y_max = y + h;
    box->area = 4 * w * h;
}

static inline void fy_box_v5(box_f_t *box, const int8_t *loc, const float *prior, const fy_scale_t *s)
{
    float x, y, w, h, anchor_x, anchor_y, anchor_w, anchor_h;

    x = s->loc_sig[loc[0]];
    y = s->loc_sig[loc[1]];
    w = s->loc_sig[loc[2]];
    h = s->loc_sig[loc[3]];

    x *= 2;
    y *= 2;
    x /= s->grid[1];
    y /= s->grid[0];

    anchor_x = prior[0];
    anchor_y = prior[1];
    anchor_w = prior[2];
    anchor_h = prior[3];

    x = x + anchor_x;
    y = y + anchor_y;

    w = 4 * w * w * anchor_w;
    h = 4 * h * h * anchor_h;

    w /= 2.;
    h /= 2.;

    box->x_min = x - w;
    box->x_max = x + w;
    box->y_min = y - h;
    box->y_max = y + h;
    box->area = 4 * w * h;
}

/* d = the four distances x1, y1, x2, y2 after the DFL projection */
static inline void fy_box_v8(box_f_t *box, const float *d, const float *prior, const fy_scale_t *s)
{
    float x1, y1, x2, y2, grid_cx, grid_cy;
    float x, y, w, h;

    x1 = d[0];
    y1 = d[1];
    x2 = d[2];
    y2 = d[3];

    grid_cx = prior[0];
    grid_cy = prior[1];

    x = ((x2 / s->grid[1]) - (x1 / s->grid[1])) / 2 + grid_cx;
    y = ((y2 / s->grid[0]) - (y1 / s->grid[0])) / 2 + grid_cy;
    w = ((x2 / s->grid[1]) + (x1 / s->grid[1]));
    h = ((y2 / s->grid[0]) + (y1 / s->grid[0]));

    w /= 2.;
    h /= 2.;

    box->x_min = x - w;
    box->x_max = x + w;
    box->y_min = y - h;
    box->y_max = y + h;
    box->area = 4 * w * h;
}

static inline void fy_box_x(box_f_t *box, const int8_t *loc, const float *prior, const fy_scale_t *s)
{
    float x, y, w, h, grid_cx, grid_cy;

    grid_cx = prior[0];
    grid_cy = prior[1];

    x = (float)loc[0] / (s->grid[1] * s->loc_scale) + grid_cx;
    y = (float)loc[1] / (s->grid[0] * s->loc_scale) + grid_cy;
    w = s->loc_exp[loc[2]] / s->grid[1];
    h = s->loc_exp[loc[3]] / s->grid[0];

    w /= 2.;
    h /= 2.;

    box->x_min = x - w;
    box->x_max = x + w;
    box->y_min = y - h;
    box->y_max = y + h;
    box->area = 4 * w * h;
}

/* DFL: softmax over reg_max values (softmax_float in detector/detector.c), then the expected position
 * (decompose_output: "sum += (coord_src[k] * k)", which the toolkit build fuses into one multiply-add).
 * Returns 0 when the toolkit code would have skipped the row (sum of the exponentials not positive). */
static inline int fy_dfl(float *d, const int8_t *loc, int reg_max, const float *exp_tbl)
{
    int side, k;
    for (side = 0; side < 4; side++, loc += reg_max) {
        float e[FY_MAX_REG], sum = 0;
        for (k = 0; k < reg_max; k++) {
            e[k] = exp_tbl[loc[k]];
            sum += e[k];
        }
        if (!(sum > 0))
            return 0;
        float r_sum = 1. / sum;
        float acc = 0;
        for (k = 0; k < reg_max; k++)
            acc = __builtin_fmaf(e[k] * r_sum, (float)k, acc);
        d[side] = acc;
    }
    return 1;
}

/* ---- the hot loop: which cells of a row have a class value at or above q?
 * The largest of the cell's class bytes is compared, sixteen bytes at a time; four cells share one test
 * because almost no cell passes. full16 = whole 16-byte blocks of class values, rest = the bytes after them. */
#define FY_INLINE static inline __attribute__((always_inline))

FY_INLINE int8x16_t fy_cell_max(const int8_t *v, const int full16, const int rest, const uint8x16_t rest_mask)
{
    int8x16_t m = vdupq_n_s8(-128);
    int i;
    for (i = 0; i < full16; i++)
        m = vmaxq_s8(m, vld1q_s8(v + 16 * i));
    if (rest)       /* the bytes behind the last class belong to the padding: they count as the lowest value */
        m = vmaxq_s8(m, vbslq_s8(rest_mask, vld1q_s8(v + 16 * full16), vdupq_n_s8(-128)));
    return m;
}

FY_INLINE int fy_scan_row(const int8_t *row, const int cols, const int cell_bytes, const int full16, const int rest,
                          const uint8x16_t rest_mask, const int q, int *hits)
{
    int n = 0, x = 0;
    for (; x + 4 <= cols; x += 4, row += 4 * cell_bytes) {
        const int8x16_t m0 = fy_cell_max(row, full16, rest, rest_mask);
        const int8x16_t m1 = fy_cell_max(row + cell_bytes, full16, rest, rest_mask);
        const int8x16_t m2 = fy_cell_max(row + 2 * cell_bytes, full16, rest, rest_mask);
        const int8x16_t m3 = fy_cell_max(row + 3 * cell_bytes, full16, rest, rest_mask);
        if (vmaxvq_s8(vmaxq_s8(vmaxq_s8(m0, m1), vmaxq_s8(m2, m3))) < q)
            continue;
        if (vmaxvq_s8(m0) >= q) hits[n++] = x;
        if (vmaxvq_s8(m1) >= q) hits[n++] = x + 1;
        if (vmaxvq_s8(m2) >= q) hits[n++] = x + 2;
        if (vmaxvq_s8(m3) >= q) hits[n++] = x + 3;
    }
    for (; x < cols; x++, row += cell_bytes)
        if (vmaxvq_s8(fy_cell_max(row, full16, rest, rest_mask)) >= q)
            hits[n++] = x;
    return n;
}

/* One copy of the loop per block count that occurs (80 classes = 5 blocks), so each is fully unrolled. */
static int fy_scan(const int8_t *row, int cols, int cell_bytes, int num_class, int q, int *hits)
{
    const int full16 = num_class / 16, rest = num_class % 16;
    uint8_t mask_bytes[16];
    int k;
    for (k = 0; k < 16; k++)
        mask_bytes[k] = k < rest ? 0xFF : 0;
    const uint8x16_t rest_mask = vld1q_u8(mask_bytes);

    switch (full16) {
    case 0: return fy_scan_row(row, cols, cell_bytes, 0, rest, rest_mask, q, hits);
    case 1: return fy_scan_row(row, cols, cell_bytes, 1, rest, rest_mask, q, hits);
    case 2: return fy_scan_row(row, cols, cell_bytes, 2, rest, rest_mask, q, hits);
    case 3: return fy_scan_row(row, cols, cell_bytes, 3, rest, rest_mask, q, hits);
    case 4: return fy_scan_row(row, cols, cell_bytes, 4, rest, rest_mask, q, hits);
    case 5: return fy_scan_row(row, cols, cell_bytes, 5, rest, rest_mask, q, hits);
    case 6: return fy_scan_row(row, cols, cell_bytes, 6, rest, rest_mask, q, hits);
    default: return fy_scan_row(row, cols, cell_bytes, full16, rest, rest_mask, q, hits);
    }
}

/* ---- the same question for layouts with objectness: one byte per anchor says whether a cell is looked at.
 * first = where the objectness byte of anchor 0 is in a cell, step = from one anchor to the next. With wide
 * cells those bytes are cache lines apart and the loop waits for memory, so the cells ahead are asked for early. */
FY_INLINE int fy_scan_obj_row(const int8_t *row, const int cols, const int cell_bytes, const int anchors,
                              const int first, const int step, const int q, const int prefetch, int *hits)
{
    int n = 0, x, k;
    for (x = 0; x < cols; x++, row += cell_bytes) {
        int any = 0;
        for (k = 0; k < anchors; k++) {
            if (prefetch)
                __builtin_prefetch(row + first + step * k + FY_PREFETCH_AHEAD, 0, 0);
            any |= row[first + step * k] >= q;
        }
        if (any)
            hits[n++] = x;
    }
    return n;
}

static int fy_scan_obj(const int8_t *row, int cols, int cell_bytes, int anchors, int first, int step, int q, int *hits)
{
    const int prefetch = cell_bytes >= 128;
    switch (anchors) {
    case 1: return fy_scan_obj_row(row, cols, cell_bytes, 1, first, step, q, prefetch, hits);
    case 3: return fy_scan_obj_row(row, cols, cell_bytes, 3, first, step, q, prefetch, hits);
    default: return fy_scan_obj_row(row, cols, cell_bytes, anchors, first, step, q, prefetch, hits);
    }
}

/* Returns 0 when it produced the detections (in the detector's own buffer, like the toolkit code),
 * -1 when this network or this frame is left to the toolkit code. Nothing is printed before it returns. */
static int fy_detect(enlight_network_t *inst, void *output_base, int *found)
{
    enlight_yolo_detector_t *p = (enlight_yolo_detector_t *)inst->post_proc_extension;
    const int num_out = p->loc_tensors_num, num_conf = p->conf_tensors_num, num_obj = p->obj_tensors_num;
    const int version = p->yolo_version, reg_max = p->dfl_reg_max;
    const int num_class = p->num_class, num_anchor = p->num_anchor;
    const float th_conf = p->th_conf, th_iou = p->th_iou;
    const int fast_nms = p->nms_method == NMS_FAST;
    int len_obj = 1, len_pri = 4, len_loc = 4, divided, i, c, k;
    fy_scale_t scale[MAX_NUM_LOC];
    fy_list_t list;
    int *hits = 0;                      /* columns of one row that hold a candidate */
    int status = -1;

    if (version == 6 || version == 8) len_obj = 0;
    if (version == 6 || version == 8 || version == 10) len_pri = 2;
    if (reg_max) len_loc = 4 * reg_max;
    if (num_out == num_conf) divided = (num_conf == num_obj) ? 2 : 1;
    else divided = 0;

    /* ---- only the layouts that were compared with the toolkit code on a board */
    if (p->softmax_use || p->sigmoid_applied || p->max_detection > 0)
        return -1;
    if (p->nms_method != NMS_DEFAULT && p->nms_method != NMS_FAST)
        return -1;
    if (num_out < 1 || num_out > MAX_NUM_LOC || num_class < 1 || num_class > 1024 || num_anchor < 1)
        return -1;
    if (divided == 0) {            /* one tensor per scale: box, objectness, classes for every anchor */
        if (!(version == 3 || version == 4 || version == 5 || version == 7) || reg_max || num_conf || num_obj)
            return -1;
    } else if (divided == 1) {     /* boxes and classes apart, no objectness, DFL (YOLOv6, YOLOv8) */
        if (!(version == 6 || version == 8) || reg_max < 1 || reg_max > FY_MAX_REG || num_anchor != 1 || num_obj)
            return -1;
    } else {                       /* boxes, classes and objectness apart (YOLOX) */
        if (version != 10 || reg_max || num_anchor != 1)
            return -1;
    }

    const int box_len = len_loc + len_obj + num_class;      /* values per anchor when everything is in one tensor */
    const int loc_box_len = divided ? len_loc : box_len;
    const int conf_box_len = divided == 0 ? box_len : divided == 1 ? len_obj + num_class : num_class;
    const int obj_box_len = divided == 0 ? box_len : divided == 1 ? len_obj + num_class : len_obj;
    const int conf_off = divided == 0 ? len_loc + len_obj : divided == 1 ? len_obj : 0;
    const int obj_off = divided == 0 ? 4 : 0;

    const float *prior = p->default_box;
    for (i = 0; i < num_out; i++) {
        fy_scale_t *s = &scale[i];
        const int tables = divided == 0 ? 1 : divided == 1 ? 2 : 3;
        int rising = 1;

        s->grid[0] = p->num_grids[2 * i];
        s->grid[1] = p->num_grids[2 * i + 1];
        if (s->grid[0] < 1 || s->grid[1] < 1)
            return -1;
        s->loc = p->loc_tensors[i];
        s->conf = divided ? p->conf_tensors[i] : s->loc;
        s->obj = divided == 2 ? p->obj_tensors[i] : s->conf;
        if (!fy_tensor_ok(s->loc, loc_box_len * num_anchor, s->grid)
            || !fy_tensor_ok(s->conf, conf_box_len * num_anchor, s->grid)
            || !fy_tensor_ok(s->obj, obj_box_len * num_anchor, s->grid))
            return -1;
        s->loc->base = (char *)output_base;
        s->conf->base = (char *)output_base;
        s->obj->base = (char *)output_base;

        s->prior = prior;
        prior += s->grid[0] * s->grid[1] * num_anchor * len_pri;

        s->conf_sig = &p->sig_tables[(tables * i) * 256 + 128];
        s->loc_sig = &p->sig_tables[(tables * i + (divided ? 1 : 0)) * 256 + 128];
        s->loc_exp = &p->exp_tables[(tables * i + (divided ? 1 : 0)) * 256 + 128];
        s->obj_sig = &p->sig_tables[(tables * i + (divided == 2 ? 2 : 0)) * 256 + 128];
        s->loc_scale = s->loc->scale;

        /* cells are picked by the objectness value, or by any class value when there is no objectness */
        s->q_pass = fy_first_passing(len_obj ? s->obj_sig : s->conf_sig, th_conf, &rising);
        if (!rising)
            return -1;
    }

    memset(&list, 0, sizeof list);

    for (i = 0, k = 0; i < num_out; i++)
        if (scale[i].grid[1] > k)
            k = scale[i].grid[1];
    hits = (int *)malloc(sizeof(int) * (size_t)k);
    if (!hits)
        return -1;

    for (i = 0; i < num_out; i++) {
        const fy_scale_t *s = &scale[i];
        const int rows = s->grid[0], cols = s->grid[1];
        const int conf_cell_bytes = (s->conf->dimensions[1] + 31) & 0xFFE0;
        const int obj_cell_bytes = (s->obj->dimensions[1] + 31) & 0xFFE0;
        int gi, h, hit_count;

        if (s->q_pass > 127)
            continue;                       /* no value can reach the threshold on this scale */

        for (gi = 0; gi < rows; gi++) {
            /* the hot loops of this file: the columns of this row that hold a candidate */
            if (len_obj)
                hit_count = fy_scan_obj(fy_cell(s->obj, gi, 0), cols, obj_cell_bytes, num_anchor, obj_off, obj_box_len, s->q_pass, hits);
            else
                hit_count = fy_scan(fy_cell(s->conf, gi, 0), cols, conf_cell_bytes, num_class, s->q_pass, hits);

            for (h = 0; h < hit_count; h++) {
                const int gj = hits[h];
                const int8_t *conf_cell = fy_cell(s->conf, gi, gj);
                const int8_t *obj_cell = len_obj ? fy_cell(s->obj, gi, gj) : 0;

                for (k = 0; k < num_anchor; k++) {
                    float obj_val = 0;
                    if (len_obj) {
                        const int8_t q = obj_cell[obj_box_len * k + obj_off];
                        if (q < s->q_pass)
                            continue;
                        obj_val = s->obj_sig[q];
                    }

                    /* ---- this (cell, anchor) is a candidate: from here on, the toolkit's arithmetic */
                    const int8_t *cls_q = conf_cell + conf_box_len * k + conf_off;
                    const int8_t *loc_q = fy_cell(s->loc, gi, gj) + loc_box_len * k;
                    const float *pri = s->prior + len_pri * (num_anchor * (cols * gi + gj) + k);
                    box_f_t box;
                    int count = 0;                  /* candidates this (cell, anchor) adds to the list */

                    if (fy_reserve(&list, fast_nms ? 1 : num_class))
                        goto done;

                    if (fast_nms) {
                        /* thresholding_by_default_box: the best class of the box, the first one on a tie */
                        int best = 0;
                        float best_val = len_obj ? s->conf_sig[cls_q[0]] * obj_val : s->conf_sig[cls_q[0]];
                        for (c = 1; c < num_class; c++) {
                            const float v = len_obj ? s->conf_sig[cls_q[c]] * obj_val : s->conf_sig[cls_q[c]];
                            if (best_val < v) {
                                best_val = v;
                                best = c;
                            }
                        }
                        if (!(best_val >= th_conf))
                            continue;
                        list.score[list.n] = best_val;
                        list.cls[list.n] = (uint16_t)best;
                        count = 1;
                    } else {
                        /* thresholding: one candidate per class over the threshold */
                        for (c = 0; c < num_class; c++) {
                            const float v = len_obj ? s->conf_sig[cls_q[c]] * obj_val : s->conf_sig[cls_q[c]];
                            if (v > th_conf) {
                                list.score[list.n + count] = v;
                                list.cls[list.n + count] = (uint16_t)c;
                                count++;
                            }
                        }
                    }
                    if (!count)
                        continue;

                    if (version == 6 || version == 8) {
                        float d[4];
                        if (!fy_dfl(d, loc_q, reg_max, s->loc_exp))
                            goto done;              /* a case the toolkit code handles in its own way */
                        fy_box_v8(&box, d, pri, s);
                    } else if (version == 5 || version == 7) {
                        fy_box_v5(&box, loc_q, pri, s);
                    } else if (version == 10) {
                        fy_box_x(&box, loc_q, pri, s);
                    } else {
                        fy_box_v3(&box, loc_q, pri, s);
                    }

                    while (count-- > 0)
                        list.box[list.n++] = box;   /* the same box for each of its classes */
                }
            }
        }
    }

    /* ---- NMS with the toolkit's own functions, fed in the toolkit's order */
    {
        detection_f_t *detection = alloc_detection_float(list.n > 0 ? list.n : 1);
        enlight_objs_t *objs = &p->detected_objects;
        const int *img = inst->img_sizes;
        int n;

        if (!detection)
            goto done;

        if (fast_nms) {
            cand_f_t cand;
            cand.box = list.box;
            cand.score = list.score;
            cand.cls = list.cls;
            cand.n = list.n;
            if (list.n > 0)
                fastnms_float(detection, &cand, th_iou);
        } else if (list.n > 0) {
            /* class by class, each class in the order its candidates were found: a stable counting sort */
            int *start = (int *)calloc((size_t)num_class + 1, sizeof(int));
            box_f_t *box = (box_f_t *)malloc(sizeof(box_f_t) * list.n);
            float *score = (float *)malloc(sizeof(float) * list.n);
            if (!start || !box || !score) {
                free(start);
                free(box);
                free(score);
                free_detection_float(detection);
                goto done;
            }
            for (i = 0; i < list.n; i++)
                start[list.cls[i] + 1]++;
            for (c = 0; c < num_class; c++)
                start[c + 1] += start[c];
            for (i = 0; i < list.n; i++) {
                const int at = start[list.cls[i]]++;
                box[at] = list.box[i];
                score[at] = list.score[i];
            }
            /* start[c] is now the end of class c */
            for (c = 0; c < num_class; c++) {
                const int begin = c ? start[c - 1] : 0;
                if (start[c] > begin) {
                    cand_f_t cand;
                    cand.box = box + begin;
                    cand.score = score + begin;
                    cand.cls = list.cls;
                    cand.n = start[c] - begin;
                    nms_float(detection, &cand, c, th_iou);
                }
            }
            free(start);
            free(box);
            free(score);
        }

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
            o->cls = detection->cls[i] + 1;     /* + 1 for background, as the toolkit code does */
            o->img_w = img[2];
            o->img_h = img[1];
        }
        free_detection_float(detection);
        status = 0;
    }

done:
    free(hits);
    fy_release(&list);
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
    if (mode == 2 || fy_detect(inst, output_base, &found) != 0)
        return run_post_process_reference(net_inst, output_base, num_input, detected_objects);

    objs = &((enlight_yolo_detector_t *)inst->post_proc_extension)->detected_objects;

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
