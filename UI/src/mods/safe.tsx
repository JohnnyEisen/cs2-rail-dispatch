// MIT License - Copyright (c) 2026 AmicusDeus (Transit Timetables)
// Adapted from TransitTimetables/UI/src/mods/safe.tsx (included in this project under the MIT
// terms of that project - see LICENSE-TRANSITTIMETABLES.txt). Error boundary: if an injected
// component throws, render nothing rather than breaking the host HUD.
import { Component } from "react";

export class Safe extends Component<{ children: any }, { err: boolean }> {
    constructor(props: any) {
        super(props);
        this.state = { err: false };
    }
    static getDerivedStateFromError() {
        return { err: true };
    }
    render() {
        return this.state.err ? null : this.props.children;
    }
}
