(function () {
    'use strict';

    var SCALE_LIMITS = {
        FAHRENHEIT: { min: 40, max: 95, suffix: '°F', label: 'Presets (°F)' },
        CELSIUS: { min: 4, max: 35, suffix: '°C', label: 'Presets (°C)' }
    };
    var MAX_PRESETS = 4;

    var listEl;
    var hiddenEl;
    var scaleEl;
    var scaleLabelEl;
    var deviceEl;
    var addBtn;
    var validationEl;
    var loadWrapped = false;
    var dropdownWrapped = false;
    var currentScale = 'FAHRENHEIT';
    var deviceScaleMap = {};

    function normalizeScale(scale) {
        return scale === 'CELSIUS' ? 'CELSIUS' : 'FAHRENHEIT';
    }

    function getLimits() {
        return SCALE_LIMITS[currentScale] || SCALE_LIMITS.FAHRENHEIT;
    }

    function fToC(value) {
        return Math.round((value - 32) * 5 / 9);
    }

    function cToF(value) {
        return Math.round(value * 9 / 5 + 32);
    }

    function convertValue(value, fromScale, toScale) {
        if (fromScale === toScale) {
            return value;
        }
        return fromScale === 'FAHRENHEIT' ? fToC(value) : cToF(value);
    }

    function rangeMessage() {
        var limits = getLimits();
        return limits.min + '–' + limits.max + ' ' + limits.suffix + ' only';
    }

    function emptyMessage() {
        var limits = getLimits();
        return 'Add at least one temperature (' + limits.min + '–' + limits.max + ' ' + limits.suffix + ').';
    }

    function temperatureAriaLabel() {
        return currentScale === 'CELSIUS'
            ? 'Preset temperature in degrees Celsius'
            : 'Preset temperature in degrees Fahrenheit';
    }

    function syncScaleField() {
        if (scaleEl) {
            scaleEl.value = currentScale;
        }
    }

    function updateScaleChrome() {
        var limits = getLimits();

        if (scaleLabelEl) {
            scaleLabelEl.textContent = limits.label;
        }

        getRowInputs().forEach(function (input) {
            input.min = String(limits.min);
            input.max = String(limits.max);
            input.placeholder = limits.min + '–' + limits.max;
            input.setAttribute('aria-label', temperatureAriaLabel());
        });

        if (listEl) {
            listEl.querySelectorAll('.preset-row-suffix').forEach(function (suffix) {
                suffix.textContent = limits.suffix;
            });
        }
    }

    function indexDeviceScales(items) {
        deviceScaleMap = {};
        if (!items || !items.length) {
            return;
        }

        items.forEach(function (item) {
            if (!item || !item.name) {
                return;
            }
            deviceScaleMap[item.name] = normalizeScale(item.temperatureScale);
        });
    }

    function scaleForSelectedDevice() {
        if (!deviceEl || !deviceEl.value) {
            return currentScale;
        }
        return deviceScaleMap[deviceEl.value] || currentScale;
    }

    function applyScale(scale, convertValues) {
        var nextScale = normalizeScale(scale);
        var previousScale = currentScale;

        if (convertValues && previousScale !== nextScale) {
            getRowInputs().forEach(function (input) {
                var row = readRowValue(input, previousScale);
                if (row.valid && row.value !== null) {
                    input.value = String(convertValue(row.value, previousScale, nextScale));
                }
            });
        }

        currentScale = nextScale;
        syncScaleField();
        updateScaleChrome();
        onRowInput();
    }

    function parsePresets(raw, scale) {
        var limits = SCALE_LIMITS[normalizeScale(scale)] || SCALE_LIMITS.FAHRENHEIT;

        if (!raw || !String(raw).trim()) {
            return [];
        }

        var values = [];
        String(raw).split(/[,;\n\r]+/).forEach(function (segment) {
            var trimmed = segment.trim();
            if (!trimmed.length) {
                return;
            }

            var n = Number(trimmed);
            if (!Number.isFinite(n)) {
                return;
            }

            n = Math.round(n);
            if (n >= limits.min && n <= limits.max) {
                values.push(n);
            }
        });

        return values.slice(0, MAX_PRESETS);
    }

    function serializePresets(values) {
        return values.join(',');
    }

    function getRowInputs() {
        if (!listEl) {
            return [];
        }
        return Array.prototype.slice.call(listEl.querySelectorAll('.preset-temp'));
    }

    function readRowValue(input, scale) {
        var limits = SCALE_LIMITS[normalizeScale(scale || currentScale)] || SCALE_LIMITS.FAHRENHEIT;
        var raw = (input.value || '').trim();
        if (!raw.length) {
            return { empty: true, valid: true, value: null };
        }

        var n = Number(raw);
        if (!Number.isFinite(n) || !Number.isInteger(n)) {
            return { empty: false, valid: false, value: null, message: 'Whole numbers only' };
        }

        if (n < limits.min || n > limits.max) {
            return {
                empty: false,
                valid: false,
                value: null,
                message: rangeMessage()
            };
        }

        return { empty: false, valid: true, value: n };
    }

    function collectValues() {
        var values = [];
        getRowInputs().forEach(function (input) {
            var row = readRowValue(input);
            if (row.valid && row.value !== null) {
                values.push(row.value);
            }
        });
        return values;
    }

    function updateValidation() {
        if (!validationEl) {
            return false;
        }

        var messages = [];
        var hasInvalid = false;
        var hasValue = false;

        getRowInputs().forEach(function (input) {
            var row = readRowValue(input);
            var rowWrap = input.closest('.preset-row');
            if (rowWrap) {
                rowWrap.classList.toggle('preset-row--invalid', !row.empty && !row.valid);
            }

            if (!row.empty && !row.valid) {
                hasInvalid = true;
                if (row.message && messages.indexOf(row.message) === -1) {
                    messages.push(row.message);
                }
            }

            if (row.valid && row.value !== null) {
                hasValue = true;
            }

            input.setAttribute('aria-invalid', (!row.empty && !row.valid) ? 'true' : 'false');
        });

        if (hasInvalid) {
            validationEl.textContent = messages.join(' · ');
            validationEl.hidden = false;
            return false;
        }

        if (!hasValue) {
            validationEl.textContent = emptyMessage();
            validationEl.hidden = false;
            return false;
        }

        validationEl.textContent = '';
        validationEl.hidden = true;
        return true;
    }

    function syncHiddenField() {
        if (!hiddenEl) {
            return;
        }
        hiddenEl.value = serializePresets(collectValues());
    }

    function onRowInput() {
        updateAddButton();
        syncScaleField();
        if (!updateValidation()) {
            syncHiddenField();
            return;
        }
        syncHiddenField();
        if (typeof setSettings === 'function') {
            setSettings();
        }
    }

    function onRowBlur(event) {
        var input = event.target;
        input.value = (input.value || '').trim();
        var row = readRowValue(input);
        if (!row.empty && row.valid && row.value !== null) {
            input.value = String(row.value);
        }
        onRowInput();
    }

    function createRow(value) {
        var limits = getLimits();
        var row = document.createElement('div');
        row.className = 'preset-row';

        var label = document.createElement('span');
        label.className = 'preset-row-index';
        label.setAttribute('aria-hidden', 'true');

        var input = document.createElement('input');
        input.type = 'number';
        input.className = 'preset-temp sdpi-item-value';
        input.min = String(limits.min);
        input.max = String(limits.max);
        input.step = '1';
        input.inputMode = 'numeric';
        input.placeholder = limits.min + '–' + limits.max;
        input.setAttribute('aria-label', temperatureAriaLabel());
        if (value !== undefined && value !== null) {
            input.value = String(value);
        }
        input.addEventListener('input', onRowInput);
        input.addEventListener('blur', onRowBlur);

        var suffix = document.createElement('span');
        suffix.className = 'preset-row-suffix';
        suffix.textContent = limits.suffix;

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'preset-remove';
        removeBtn.title = 'Remove preset';
        removeBtn.setAttribute('aria-label', 'Remove preset');
        removeBtn.textContent = '×';
        removeBtn.addEventListener('click', function () {
            row.remove();
            renumberRows();
            updateAddButton();
            onRowInput();
        });

        row.appendChild(label);
        row.appendChild(input);
        row.appendChild(suffix);
        row.appendChild(removeBtn);
        return row;
    }

    function renumberRows() {
        if (!listEl) {
            return;
        }
        var rows = listEl.querySelectorAll('.preset-row');
        Array.prototype.forEach.call(rows, function (row, index) {
            var label = row.querySelector('.preset-row-index');
            if (label) {
                label.textContent = String(index + 1) + '.';
            }
        });
    }

    function updateAddButton() {
        if (!addBtn || !listEl) {
            return;
        }
        var count = listEl.querySelectorAll('.preset-row').length;
        addBtn.disabled = count >= MAX_PRESETS;
    }

    function addRow(value) {
        if (!listEl) {
            return;
        }
        if (listEl.querySelectorAll('.preset-row').length >= MAX_PRESETS) {
            return;
        }
        listEl.appendChild(createRow(value));
        renumberRows();
        updateAddButton();
    }

    function clearRows() {
        if (!listEl) {
            return;
        }
        listEl.innerHTML = '';
    }

    function renderFromValues(values) {
        clearRows();
        if (!values.length) {
            addRow();
            return;
        }
        values.forEach(function (v) {
            addRow(v);
        });
    }

    function loadFromHidden() {
        if (!hiddenEl) {
            return;
        }
        renderFromValues(parsePresets(hiddenEl.value, currentScale));
        updateValidation();
        updateAddButton();
    }

    function onDeviceChanged() {
        applyScale(scaleForSelectedDevice(), true);
    }

    function wrapLoadConfiguration() {
        if (loadWrapped || typeof loadConfiguration !== 'function') {
            return;
        }
        loadWrapped = true;
        var original = loadConfiguration;
        loadConfiguration = function (payload, isglobal) {
            original(payload, isglobal);
            if (!isglobal) {
                if (payload && payload.temperatureScale) {
                    currentScale = normalizeScale(payload.temperatureScale);
                    syncScaleField();
                    updateScaleChrome();
                }
                loadFromHidden();
                onDeviceChanged();
            }
        };
    }

    function wrapPopulateDeviceDropdown() {
        if (dropdownWrapped || typeof populateDeviceDropdown !== 'function') {
            return;
        }
        dropdownWrapped = true;
        var original = populateDeviceDropdown;
        populateDeviceDropdown = function (piDevicesPayload) {
            var items = [];
            try {
                if (Array.isArray(piDevicesPayload)) {
                    items = piDevicesPayload;
                } else if (typeof piDevicesPayload === 'string' && piDevicesPayload.length > 0) {
                    items = JSON.parse(piDevicesPayload);
                }
            } catch (err) {
                items = [];
            }

            indexDeviceScales(items);
            original(piDevicesPayload);
            onDeviceChanged();
        };
    }

    wrapLoadConfiguration();
    wrapPopulateDeviceDropdown();

    function init() {
        listEl = document.getElementById('presetRows');
        hiddenEl = document.getElementById('presets');
        scaleEl = document.getElementById('temperatureScale');
        scaleLabelEl = document.getElementById('presetScaleLabel');
        deviceEl = document.getElementById('device');
        addBtn = document.getElementById('addPresetBtn');
        validationEl = document.getElementById('presetValidation');

        if (!listEl || !hiddenEl) {
            return;
        }

        if (scaleEl && scaleEl.value) {
            currentScale = normalizeScale(scaleEl.value);
        }

        if (deviceEl) {
            deviceEl.addEventListener('change', function () {
                onDeviceChanged();
                if (typeof setSettings === 'function') {
                    setSettings();
                }
            });
        }

        updateScaleChrome();

        if (addBtn) {
            addBtn.addEventListener('click', function () {
                addRow();
                var inputs = getRowInputs();
                if (inputs.length) {
                    inputs[inputs.length - 1].focus();
                }
                onRowInput();
            });
        }

        if (!hiddenEl.value || !String(hiddenEl.value).trim()) {
            addRow();
            updateValidation();
        } else {
            loadFromHidden();
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
