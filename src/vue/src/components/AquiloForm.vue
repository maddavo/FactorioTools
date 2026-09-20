<template>
  <fieldset class="border p-3 mb-3">
    <legend>Aquilo</legend>
    <div class="form-check">
      <input type="checkbox" class="form-check-input" id="add-heat-pipes" v-model="addHeatPipes">
      <label class="form-check-label" for="add-heat-pipes">Add heat pipes</label>
    </div>
    <small class="text-muted">Creates one connected heat-pipe network beside planned pumpjacks, fluid pipes, and beacons. Connect it to a heating tower or reactor.</small>
  </fieldset>
</template>

<script lang="ts">
import { storeToRefs } from 'pinia';
import { pick } from '../lib/helpers';
import { useOilFieldStore } from '../stores/OilFieldStore';

export default {
  data() {
    return pick(storeToRefs(useOilFieldStore()), 'addHeatPipes');
  }
}
</script>
