// Minimal ambient declarations for the game-provided modules we import.
// We declare ONLY what this project actually uses (no copied type stubs).
declare module "cs2/modding" {
    export type ModRegistrar = (moduleRegistry: ModuleRegistry) => void;

    export interface ModuleRegistry {
        append(modulePath: string, exportNameOrComponent: any, appendedComponent?: any, index?: number): void;
        extend(modulePath: string, exportName: string, extendCb: any): void;
        override(modulePath: string, exportName: string, newValue: any): void;
    }
}

declare module "cs2/ui" {
    import type * as React from "react";

    // Subset of the game's ButtonProps that we use.
    // Verified against TransitTimetables/UI/types/ui.d.ts:
    //   export const FloatingButton: (props: Partial<IconButtonProps>) => JSX.Element;
    //   interface IconButtonProps extends ButtonProps { src: string; tinted?: boolean; theme?: ... }
    //   ButtonProps carries tooltipLabel?: ReactNode, onSelect?: () => void, disabled?, selected?, className?
    export interface IconButtonProps {
        src: string;
        tinted?: boolean;
        tooltipLabel?: React.ReactNode;
        onSelect?: () => void;
        disabled?: boolean;
        selected?: boolean;
        className?: string;
        as?: "button" | "div";
    }

    export const FloatingButton: (props: Partial<IconButtonProps>) => JSX.Element;
}
